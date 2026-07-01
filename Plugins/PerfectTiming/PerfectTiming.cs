namespace PerfectTiming
{
    using ExileBridge;
    using ImGuiNET;
    using Newtonsoft.Json;
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Numerics;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    ///     PerfectTiming — auto hold+release for charge/perfect skills (Warrior "Perfect Strike",
    ///     Ranger "Snipe", etc.). Each <see cref="SkillProfile" /> anchors on its windup AnimationId
    ///     and releases at a precise, tuned delay via a Stopwatch, reading the perfect/normal outcome
    ///     AnimationId as feedback to auto-tune. The SDK doesn't expose the Actor component, so the
    ///     live AnimationId is read via raw memory.
    /// </summary>
    public sealed class PerfectTiming : Plugin<PerfectTimingSettings>
    {
        // ActorOffset (from GameOffsets.Objects.Components.ActorOffset).
        private const int EntityDetailsOffset = 0x08;
        private const int ActorAnimationIdOffset = 0x8B0;
        private const int ActorActiveSkillsFirst = 0xB08;
        private const int ActiveSkillStride = 0x10;
        private const int SkillUseStageOffset = 0x08;
        private const int SkillCastTypeOffset = 0x0C;
        private const int SkillIdInfoOffset = 0x40;
        private const int SkillGrantedRowOffset = 0x48;
        private const int SkillStructBytes = 0x100;
        private const int ActorWindowStart = 0x880;
        private const int ActorWindowBytes = 0x80;

        // Full-Actor dump for hunting the "perfect window" / charge value the game must update.
        private const int ActorWideStart = 0x000;
        private const int ActorWideBytes = 0xE00;

        // Stats component (StatsOffsets): two stat layers, each a StatsStructInternal whose stat
        // vector (StatArrayStruct {int key; int value}) is at +0xF8.
        private const int StatsItemsLayerOffset = 0x160;
        private const int StatsBuffLayerOffset = 0x1C8;
        private const int StatsVectorInStruct = 0xF8;

        // Attack-speed-related stat ids (GameStats.cs; the grepped values are "<n> + 1").
        private static readonly int[] SpeedStatIds = { 71, 72, 73, 79, 284, 327, 331, 338, 358, 1647, 1909 };

        // Auto-tune: walk the delay down (misses cluster at max windup) and wrap when it under-shoots.
        private const int TuneStep = 8;
        private const int TuneFloor = 300;
        private const int TuneCeil = 620;

        private int capturingTriggerIdx = -1;
        private int capturingSkillIdx = -1;

        private readonly List<int> recentAnims = new();

        private StreamWriter csv;
        private long logStartMs;
        private IntPtr lastActor;

        private string SettingPathname => Path.Join(this.DirectoryPath, "config", "settings.txt");
        private string LogPathname => Path.Join(this.DirectoryPath, "config", "probe_log.csv");

        private struct SkillInfo
        {
            public string Name;
            public int UseStage;
            public int CastType;
            public IntPtr Details;
        }

        public override void OnEnable(bool isGameAttached)
        {
            if (File.Exists(this.SettingPathname))
            {
                var content = File.ReadAllText(this.SettingPathname);
                this.Settings = JsonConvert.DeserializeObject<PerfectTimingSettings>(content) ?? this.Settings;
            }

            this.Settings.Profiles ??= new List<SkillProfile>();
            if (this.Settings.Profiles.Count == 0)
            {
                // Migrate the legacy single-skill (v1) config into the first profile.
                this.Settings.Profiles.Add(new SkillProfile
                {
                    Name = "Perfect Strike",
                    Enabled = true,
                    TriggerKey = this.Settings.TriggerKey,
                    SkillIsMouse = this.Settings.SkillIsMouse,
                    SkillMouseButton = this.Settings.SkillMouseButton,
                    SkillKey = this.Settings.SkillKey,
                    WindupAnim = this.Settings.WindupAnim,
                    PerfectAnim = this.Settings.PerfectAnim,
                    EndAnim = this.Settings.EndAnim,
                    ReleaseDelayMs = this.Settings.ReleaseDelayMs,
                    RepeatWhileHeld = this.Settings.RepeatWhileHeld,
                    AutoTune = this.Settings.AutoTune,
                });
            }

            foreach (var p in this.Settings.Profiles)
            {
                p.Recent ??= new Queue<bool>();
            }
        }

        public override void OnDisable()
        {
            this.ReleaseAllProfiles();
            this.StopLog();
            Mem.Close();
        }

        public override void SaveSettings()
        {
            var dir = Path.GetDirectoryName(this.SettingPathname);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(this.SettingPathname, JsonConvert.SerializeObject(this.Settings, Formatting.Indented));
        }

        public override void DrawUI()
        {
            if (!this.Ctx.Game.IsInGame)
            {
                this.ReleaseAllProfiles();
                return;
            }

            Mem.Pid = this.Ctx.Game.Pid;
            var player = this.Ctx.Game.InGame.Player;
            if (player == null)
            {
                return;
            }

            var actor = ResolveComponent(player.Address, "Actor");
            if (actor == IntPtr.Zero)
            {
                this.ReleaseAllProfiles();
                if (this.Settings.ShowProbe)
                {
                    this.DrawProbe(IntPtr.Zero, int.MinValue, new List<SkillInfo>(), null, null, null);
                }

                return;
            }

            int animId = Mem.I32(actor + ActorAnimationIdOffset);
            this.lastActor = actor;
            this.TrackAnim(animId);

            // Only drive input while the GAME is the foreground window — otherwise the synthetic
            // key/mouse events would go to whatever other window has focus (settings, another app).
            bool foreground = this.Ctx.Game.IsForeground;
            foreach (var p in this.Settings.Profiles)
            {
                if (!this.Settings.AutoEnabled || !p.Enabled || !foreground)
                {
                    if (p.SkillHeld)
                    {
                        this.SkillUp(p);
                    }

                    p.Phase = 0;
                    continue;
                }

                this.UpdateAuto(p, animId, actor);
            }

            if (this.Settings.ShowProbe || this.csv != null)
            {
                var skills = this.ReadSkills(actor);
                SkillInfo? target = PickTarget(skills, this.Settings.TargetSkillMatch);
                var statsComp = ResolveComponent(player.Address, "Stats");
                var itemsStats = ReadStatLayer(statsComp, StatsItemsLayerOffset);
                var buffStats = ReadStatLayer(statsComp, StatsBuffLayerOffset);
                if (this.csv != null)
                {
                    this.LogFrame(actor, animId, target, buffStats);
                }

                if (this.Settings.ShowProbe)
                {
                    this.DrawProbe(actor, animId, skills, target, itemsStats, buffStats);
                }
            }
        }

        private void TrackAnim(int animId)
        {
            if (this.recentAnims.Count == 0 || this.recentAnims[^1] != animId)
            {
                this.recentAnims.Add(animId);
                while (this.recentAnims.Count > 12)
                {
                    this.recentAnims.RemoveAt(0);
                }
            }
        }

        // ── Per-profile auto hold + release state machine ───────────────────────────
        private void UpdateAuto(SkillProfile p, int animId, IntPtr actor)
        {
            bool triggerDown = p.TriggerKey != 0 && Input.IsKeyDown(p.TriggerKey);
            bool edge = triggerDown && !p.TriggerWasDown;
            p.TriggerWasDown = triggerDown;

            long now = Stopwatch.GetTimestamp();
            switch (p.Phase)
            {
                case 0: // Idle
                    if (triggerDown && (p.RepeatWhileHeld || edge))
                    {
                        if (p.OnlyOnMonster && !this.HasLiveMonsterNearCursor(p.CursorRadius))
                        {
                            break; // cursor not on a live monster (looting / ground click) — don't attack
                        }

                        this.SkillDown(p);
                        p.PressTicks = now;
                        p.Phase = 1;
                    }

                    break;

                case 1: // WaitWindup
                    if (!triggerDown)
                    {
                        if (p.SkillHeld)
                        {
                            this.SkillUp(p);
                        }

                        p.Phase = 0;
                    }
                    else if (animId == p.WindupAnim)
                    {
                        p.WindupStartTicks = now;
                        if (p.UseChargeValue)
                        {
                            // Charge-based: an off-thread tight-poll releases at the exact threshold crossing.
                            p.ProbeStrike = false;
                            this.ScheduleChargeRelease(p, actor);
                            p.Phase = 2;
                        }
                        else
                        {
                            bool probe = p.ScaleToWindup && (p.WindupEst <= 0 || (p.StrikeCount % 8 == 0));
                            p.ProbeStrike = probe;
                            if (!probe)
                            {
                                int delay = (p.ScaleToWindup && p.WindupRef > 0 && p.WindupEst > 0)
                                    ? (int)Math.Clamp(p.ReleaseDelayMs * (p.WindupEst / p.WindupRef), 60, 3000)
                                    : p.ReleaseDelayMs;
                                p.StrikeDelayMs = delay;
                                this.ScheduleRelease(p, delay);
                            }

                            p.Phase = 2;
                        }
                    }
                    else if (ElapsedMs(p.PressTicks, now) > 700)
                    {
                        if (p.SkillHeld)
                        {
                            this.SkillUp(p);
                        }

                        p.Phase = 0;
                    }

                    break;

                case 2: // Charging
                    if (p.UseChargeValue)
                    {
                        // The off-thread tight-poll fires SkillUp at the exact crossing; wait for it.
                        if (!p.ReleaseScheduled)
                        {
                            p.OutcomeStart = now;
                            p.Phase = 3;
                        }
                    }
                    else if (p.ProbeStrike)
                    {
                        // Probe: let the windup end on its own to measure its length (and fire one normal hit),
                        // releasing the instant its outcome anim appears.
                        if (animId == p.PerfectAnim || animId == p.EndAnim)
                        {
                            if (p.SkillHeld)
                            {
                                this.SkillUp(p);
                            }

                            long tOut = ElapsedMs(p.WindupStartTicks, now);
                            p.WindupEst = p.WindupEst <= 0 ? tOut : ((0.5 * tOut) + (0.5 * p.WindupEst));
                            if (p.WindupRef <= 0)
                            {
                                p.WindupRef = p.WindupEst; // auto-anchor the scaling on the first measurement
                            }

                            p.StrikeCount++;
                            p.ProbeStrike = false;
                            this.EndCycle(p, triggerDown);
                        }
                        else if (ElapsedMs(p.WindupStartTicks, now) > 2500)
                        {
                            if (p.SkillHeld)
                            {
                                this.SkillUp(p);
                            }

                            p.Phase = 0;
                        }
                    }
                    else if (!p.ReleaseScheduled)
                    {
                        p.OutcomeStart = now;
                        p.Phase = 3;
                    }

                    break;

                case 3: // WaitOutcome (timed release already fired)
                    if (animId == p.PerfectAnim || animId == p.EndAnim)
                    {
                        long tOut = ElapsedMs(p.WindupStartTicks, now);
                        // Opportunistic re-measure: if the game released before our scheduled delay
                        // (attack speed went up), tOut is the shorter natural windup.
                        if (p.ScaleToWindup && tOut < p.StrikeDelayMs - 40)
                        {
                            p.WindupEst = p.WindupEst <= 0 ? tOut : ((0.5 * tOut) + (0.5 * p.WindupEst));
                            if (p.WindupRef <= 0)
                            {
                                p.WindupRef = p.WindupEst;
                            }
                        }

                        this.RecordOutcome(p, animId == p.PerfectAnim, tOut);
                        this.EndCycle(p, triggerDown);
                    }
                    else if (ElapsedMs(p.OutcomeStart, now) > 500)
                    {
                        this.EndCycle(p, triggerDown);
                    }

                    break;
            }
        }

        // True if a live (non-friendly) monster is within `radius` screen pixels of the cursor.
        private bool HasLiveMonsterNearCursor(float radius)
        {
            var mp = ImGui.GetMousePos();
            float r2 = radius * radius;
            foreach (var e in this.Ctx.Entities.Awake)
            {
                if (e == null || !e.IsValid || e.Type != EntityType.Monster)
                {
                    continue;
                }

                if (e.TryGetComponent<IPositioned>(out var pos) && pos.IsFriendly)
                {
                    continue;
                }

                if (!e.TryGetComponent<ILife>(out var life) || life.Health.Current <= 0)
                {
                    continue;
                }

                if (!e.TryGetComponent<IRender>(out var r))
                {
                    continue;
                }

                var wp = r.WorldPosition;
                wp.Z -= r.ModelBounds.Z * 0.5f; // mid-body: better matches the cursor over the monster
                var sp = this.Ctx.Render.WorldToScreen(wp);
                if (Vector2.DistanceSquared(sp, mp) <= r2)
                {
                    return true;
                }
            }

            return false;
        }

        private void EndCycle(SkillProfile p, bool triggerDown)
        {
            if (this.Settings.AutoEnabled && p.RepeatWhileHeld && triggerDown)
            {
                this.SkillDown(p);
                p.PressTicks = Stopwatch.GetTimestamp();
                p.Phase = 1;
            }
            else
            {
                p.Phase = 0;
            }
        }

        private void RecordOutcome(SkillProfile p, bool perfect, long tOut)
        {
            p.StrikeCount++;
            p.LastPerfect = perfect;
            if (perfect)
            {
                p.Hits++;
            }
            else
            {
                p.Misses++;
            }

            p.Recent.Enqueue(perfect);
            while (p.Recent.Count > 20)
            {
                p.Recent.Dequeue();
            }
        }

        // Charge-based release: tight-poll the live animation-phase value off-thread and release the
        // instant it crosses the threshold (~microsecond precision vs the 16ms render frame).
        private void ScheduleChargeRelease(SkillProfile p, IntPtr actor)
        {
            p.ReleaseScheduled = true;
            var addr = (IntPtr)(actor.ToInt64() + p.ChargeOffset);
            float threshold = p.PerfectPhase;
            long start = Stopwatch.GetTimestamp();
            long timeout = start + (long)(2.0 * Stopwatch.Frequency);
            Task.Run(() =>
            {
                try
                {
                    while (true)
                    {
                        float charge = Mem.Read<float>(addr);
                        if (charge >= threshold && charge <= 2f)
                        {
                            this.SkillUp(p);
                            break;
                        }

                        if (Stopwatch.GetTimestamp() > timeout)
                        {
                            this.SkillUp(p);
                            break;
                        }

                        // Coarse wait while far from the threshold, tight spin as it approaches.
                        if (charge < threshold - 0.06f)
                        {
                            Thread.Sleep(2);
                        }
                        else
                        {
                            Thread.SpinWait(400);
                        }
                    }
                }
                finally
                {
                    p.ReleaseScheduled = false;
                }
            });
        }

        private void ScheduleRelease(SkillProfile p, int delayMs)
        {
            p.ReleaseScheduled = true;
            long start = Stopwatch.GetTimestamp();
            long target = start + (long)(delayMs / 1000.0 * Stopwatch.Frequency);
            Task.Run(() =>
            {
                try
                {
                    int coarse = delayMs - 6;
                    if (coarse > 0)
                    {
                        Thread.Sleep(coarse);
                    }

                    while (Stopwatch.GetTimestamp() < target)
                    {
                        Thread.SpinWait(40);
                    }

                    this.SkillUp(p);
                }
                finally
                {
                    p.ReleaseScheduled = false;
                }
            });
        }

        private void SkillDown(SkillProfile p)
        {
            if (p.SkillHeld)
            {
                return;
            }

            p.SkillHeld = true;
            long token = ++p.HoldToken;
            if (p.SkillIsMouse)
            {
                Input.MouseDown((Input.MouseButton)Math.Clamp(p.SkillMouseButton, 0, 2));
            }
            else if (p.SkillKey != 0)
            {
                Input.KeyDown(p.SkillKey);
            }

            // Watchdog: guarantee a release even if the render loop stalls (overlay hidden / alt-tab /
            // menu) before the timed release fires. No-op if this hold already ended (token mismatch).
            int maxHoldMs = Math.Max(1500, p.ReleaseDelayMs + 900);
            Task.Run(() =>
            {
                Thread.Sleep(maxHoldMs);
                if (p.SkillHeld && p.HoldToken == token)
                {
                    this.SkillUp(p);
                }
            });
        }

        private void SkillUp(SkillProfile p)
        {
            if (!p.SkillHeld)
            {
                return;
            }

            p.SkillHeld = false;
            if (p.SkillIsMouse)
            {
                Input.MouseUp((Input.MouseButton)Math.Clamp(p.SkillMouseButton, 0, 2));
            }
            else if (p.SkillKey != 0)
            {
                Input.KeyUp(p.SkillKey);
            }
        }

        private void ReleaseAllProfiles()
        {
            if (this.Settings.Profiles == null)
            {
                return;
            }

            foreach (var p in this.Settings.Profiles)
            {
                if (p.SkillHeld)
                {
                    this.SkillUp(p);
                }

                p.Phase = 0;
            }
        }

        private static long ElapsedMs(long startTicks, long nowTicks)
            => (nowTicks - startTicks) * 1000 / Stopwatch.Frequency;

        // ── Settings UI ─────────────────────────────────────────────────────────────
        public override void DrawSettings()
        {
            ImGui.TextWrapped("Auto hold+release for charge/perfect skills (Perfect Strike, Snipe, ...). " +
                              "Each profile has its own trigger key, output, animation ids and release delay.");
            ImGui.Checkbox("Enable auto (master)", ref this.Settings.AutoEnabled);
            ImGui.SameLine();
            ImGui.TextDisabled($"{this.Settings.Profiles.Count} profile(s)");
            if (ImGui.Button("Add skill profile"))
            {
                this.Settings.Profiles.Add(new SkillProfile { Name = $"Skill {this.Settings.Profiles.Count + 1}", Enabled = false });
            }

            int toDelete = -1;
            if (ImGui.BeginTabBar("profiles", ImGuiTabBarFlags.AutoSelectNewTabs))
            {
                for (int i = 0; i < this.Settings.Profiles.Count; i++)
                {
                    var p = this.Settings.Profiles[i];
                    bool open = true;
                    var label = string.IsNullOrEmpty(p.Name) ? $"#{i}" : p.Name;
                    if (ImGui.BeginTabItem($"{label}##tab{i}", ref open, ImGuiTabItemFlags.NoAssumedClosure))
                    {
                        this.DrawProfile(p, i);
                        ImGui.EndTabItem();
                    }

                    if (!open)
                    {
                        toDelete = i;
                    }
                }

                ImGui.EndTabBar();
            }

            if (toDelete >= 0 && toDelete < this.Settings.Profiles.Count)
            {
                var pp = this.Settings.Profiles[toDelete];
                if (pp.SkillHeld)
                {
                    this.SkillUp(pp);
                }

                this.Settings.Profiles.RemoveAt(toDelete);
            }

            ImGui.Separator();
            if (ImGui.CollapsingHeader("Probe / diagnostics"))
            {
                ImGui.TextDisabled("Recent AnimationIds (hold a skill: the sustained id is the windup; " +
                                   "the brief ids after release are the perfect / normal outcomes):");
                ImGui.Text(string.Join("  ->  ", this.recentAnims));
                ImGui.Checkbox("Show probe window", ref this.Settings.ShowProbe);
                ImGui.InputText("Target skill match", ref this.Settings.TargetSkillMatch, 64);
                ImGui.SliderInt("Slots to show", ref this.Settings.SlotsToShow, 8, 64);
                if (ImGui.Button(this.csv != null ? "Stop logging" : "Start logging"))
                {
                    if (this.csv != null)
                    {
                        this.StopLog();
                    }
                    else
                    {
                        this.StartLog();
                    }
                }

                ImGui.SameLine();
                ImGui.TextDisabled(this.csv != null ? "logging → config/probe_log.csv" : "not logging");
            }
        }

        private void DrawProfile(SkillProfile p, int i)
        {
            ImGui.InputText($"Name##n{i}", ref p.Name, 32);
            ImGui.Checkbox($"Enabled##e{i}", ref p.Enabled);

            ImGui.Text($"Trigger key: {KeyName(p.TriggerKey)}");
            ImGui.SameLine();
            if (ImGui.Button(this.capturingTriggerIdx == i ? $"press a key...##t{i}" : $"Set##t{i}"))
            {
                this.capturingTriggerIdx = i;
            }

            if (this.capturingTriggerIdx == i && Input.TryCaptureKey(out var tvk))
            {
                p.TriggerKey = tvk;
                this.capturingTriggerIdx = -1;
            }

            ImGui.Checkbox($"Skill is a mouse button##m{i}", ref p.SkillIsMouse);
            if (p.SkillIsMouse)
            {
                string[] mb = { "Left", "Right", "Middle" };
                ImGui.Combo($"Mouse button##mb{i}", ref p.SkillMouseButton, mb, mb.Length);
            }
            else
            {
                ImGui.Text($"Skill key: {KeyName(p.SkillKey)}");
                ImGui.SameLine();
                if (ImGui.Button(this.capturingSkillIdx == i ? $"press a key...##s{i}" : $"Set##s{i}"))
                {
                    this.capturingSkillIdx = i;
                }

                if (this.capturingSkillIdx == i && Input.TryCaptureKey(out var svk))
                {
                    p.SkillKey = svk;
                    this.capturingSkillIdx = -1;
                }
            }

            ImGui.InputInt($"Windup anim##w{i}", ref p.WindupAnim);
            ImGui.InputInt($"Perfect anim##p{i}", ref p.PerfectAnim);
            ImGui.InputInt($"Normal (end) anim##end{i}", ref p.EndAnim);
            Draw.ToolTip("Use the probe: hold the skill to see the sustained windup id; release to see the perfect vs normal ids.");

            ImGui.Checkbox($"Only fire on a live monster under cursor##om{i}", ref p.OnlyOnMonster);
            if (p.OnlyOnMonster)
            {
                ImGui.SliderFloat($"Cursor radius (px)##cr{i}", ref p.CursorRadius, 20f, 220f);
            }

            ImGui.Checkbox($"Release on charge value (recommended)##uc{i}", ref p.UseChargeValue);
            Draw.ToolTip("Releases exactly when the live animation-charge value crosses the threshold — precise " +
                         "and independent of attack speed. Set the threshold once and it works at any speed.");
            if (p.UseChargeValue)
            {
                float live = this.lastActor != IntPtr.Zero
                    ? Mem.Read<float>((IntPtr)(this.lastActor.ToInt64() + p.ChargeOffset))
                    : 0f;
                ImGui.SliderFloat($"Perfect charge threshold##pp{i}", ref p.PerfectPhase, 0.20f, 0.49f, "%.3f");
                Draw.ToolTip("Release when the charge reaches this. Hold the skill and watch 'live charge' ramp; " +
                             "set the threshold just under where a full windup peaks (~0.43 for Perfect Strike).");
                ImGui.TextDisabled($"live charge (hold the skill to watch it ramp): {live:0.000}");
                ImGui.InputInt($"Charge offset##co{i}", ref p.ChargeOffset);
            }
            else
            {
                if (ImGui.SliderInt($"Release delay (ms)##d{i}", ref p.ReleaseDelayMs, 120, 1200) &&
                    p.ScaleToWindup && p.WindupEst > 0)
                {
                    p.WindupRef = p.WindupEst;
                }

                ImGui.Checkbox($"Scale delay with attack speed##sw{i}", ref p.ScaleToWindup);
                if (p.ScaleToWindup)
                {
                    int eff = (p.WindupRef > 0 && p.WindupEst > 0)
                        ? (int)(p.ReleaseDelayMs * (p.WindupEst / p.WindupRef))
                        : p.ReleaseDelayMs;
                    ImGui.TextDisabled($"windup {p.WindupEst:F0} ms | anchored {p.WindupRef:F0} ms | firing {eff} ms");
                }
            }

            ImGui.Checkbox($"Repeat while held##r{i}", ref p.RepeatWhileHeld);

            ImGui.Separator();
            int rec = p.Recent.Count;
            int recHits = 0;
            foreach (var b in p.Recent)
            {
                if (b) recHits++;
            }

            ImGui.Text($"Perfect: {p.Hits}   Miss: {p.Misses}   Last: {(p.LastPerfect ? "PERFECT" : "-")}");
            ImGui.Text($"Recent perfect rate: {(rec > 0 ? 100 * recHits / rec : 0)}%   Phase:{p.Phase}  Held:{p.SkillHeld}");
            if (ImGui.Button($"Reset stats##rs{i}"))
            {
                p.Hits = 0;
                p.Misses = 0;
                p.Recent.Clear();
                p.WindowHits = 0;
                p.WindowCount = 0;
                p.LastRate = -1;
                p.TuneDir = 0;
            }
        }

        private static string KeyName(int vk) => vk == 0 ? "(none)" : $"0x{vk:X2} ({vk})";

        // ── Probe (kept for RE / finding a new skill's animation ids) ───────────────
        private static SkillInfo? PickTarget(List<SkillInfo> skills, string match)
        {
            foreach (var s in skills)
            {
                if (!string.IsNullOrEmpty(match) && s.Name.IndexOf(match, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return s;
                }
            }

            return skills.Count > 0 ? skills[0] : (SkillInfo?)null;
        }

        private void DrawProbe(IntPtr actor, int animId, List<SkillInfo> skills, SkillInfo? target,
            Dictionary<int, int> itemsStats, Dictionary<int, int> buffStats)
        {
            ImGui.SetNextWindowBgAlpha(0.9f);
            if (ImGui.Begin("Perfect Timing Probe"))
            {
                if (actor == IntPtr.Zero)
                {
                    ImGui.Text("Actor component not found on player.");
                    ImGui.End();
                    return;
                }

                ImGui.Text($"AnimationId: {animId}  (0x{animId:X})");
                ImGui.Text($"Recent: {string.Join(" -> ", this.recentAnims)}");
                ImGui.Text($"Active skills: {skills.Count}");
                ImGui.Separator();
                if (target.HasValue)
                {
                    var t = target.Value;
                    ImGui.Text($"Target: {t.Name}");
                    ImGui.Text($"UseStage: {t.UseStage}    CastType: {t.CastType}");

                    int slots = Math.Clamp(this.Settings.SlotsToShow, 1, SkillStructBytes / 4);
                    var bytes = Mem.ReadBytes(t.Details, slots * 4);
                    if (bytes.Length >= slots * 4 &&
                        ImGui.BeginTable("slots", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY, new Vector2(0f, 220f)))
                    {
                        ImGui.TableSetupColumn("offset");
                        ImGui.TableSetupColumn("int32");
                        ImGui.TableSetupColumn("float");
                        ImGui.TableHeadersRow();
                        for (int j = 0; j < slots; j++)
                        {
                            int iv = BitConverter.ToInt32(bytes, j * 4);
                            float fv = BitConverter.ToSingle(bytes, j * 4);
                            ImGui.TableNextColumn();
                            ImGui.Text($"0x{j * 4:X2}");
                            ImGui.TableNextColumn();
                            ImGui.Text(iv.ToString());
                            ImGui.TableNextColumn();
                            ImGui.Text(float.IsFinite(fv) ? fv.ToString("0.###") : "-");
                        }

                        ImGui.EndTable();
                    }
                }

                if (buffStats != null && itemsStats != null)
                {
                    ImGui.Separator();
                    ImGui.Text("Attack-speed stats (id: items + buff):");
                    long sum = 0;
                    foreach (var id in SpeedStatIds)
                    {
                        int iv = itemsStats.TryGetValue(id, out var a) ? a : 0;
                        int bv = buffStats.TryGetValue(id, out var b) ? b : 0;
                        if (iv != 0 || bv != 0)
                        {
                            ImGui.Text($"  {id}: {iv} + {bv}");
                        }

                        sum += (id == 338) ? -(iv + bv) : (iv + bv);
                    }

                    ImGui.Text($"  -> total attack speed %: {sum}   (buff-layer entries: {buffStats.Count})");
                    ImGui.TextDisabled("Change your attack speed (frenzy / a buff) and watch this total move.");
                }
            }

            ImGui.End();
        }

        private List<SkillInfo> ReadSkills(IntPtr actor)
        {
            var result = new List<SkillInfo>();
            var first = Mem.Ptr(actor + ActorActiveSkillsFirst);
            var last = Mem.Ptr(actor + ActorActiveSkillsFirst + 8);
            if (first == IntPtr.Zero || last == IntPtr.Zero)
            {
                return result;
            }

            long count = (last.ToInt64() - first.ToInt64()) / ActiveSkillStride;
            if (count <= 0 || count > 128)
            {
                return result;
            }

            for (long i = 0; i < count; i++)
            {
                var slot = (IntPtr)(first.ToInt64() + (i * ActiveSkillStride));
                var details = Mem.Ptr(slot);
                if (details == IntPtr.Zero)
                {
                    continue;
                }

                uint idInfo = (uint)Mem.I32(details + SkillIdInfoOffset);
                var grantedRow = Mem.Ptr(details + SkillGrantedRowOffset);
                if (grantedRow == IntPtr.Zero || (idInfo >> 16) < 0x8000)
                {
                    continue;
                }

                var name = Mem.ReadUnicode(Mem.Ptr(grantedRow), 64);
                if (string.IsNullOrEmpty(name))
                {
                    name = $"?0x{idInfo:X}";
                }

                result.Add(new SkillInfo
                {
                    Name = name,
                    UseStage = Mem.I32(details + SkillUseStageOffset),
                    CastType = Mem.I32(details + SkillCastTypeOffset),
                    Details = details,
                });
            }

            return result;
        }

        private void LogFrame(IntPtr actor, int animId, SkillInfo? target, Dictionary<int, int> buffStats)
        {
            long t = Environment.TickCount64 - this.logStartMs;
            var actorHex = ToHex(Mem.ReadBytes(actor + ActorWideStart, ActorWideBytes));
            this.csv.WriteLine($"{t},{animId},{actorHex}");
        }

        private void StartLog()
        {
            var dir = Path.GetDirectoryName(this.LogPathname);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            this.csv = new StreamWriter(this.LogPathname, append: false) { AutoFlush = true };
            this.csv.WriteLine("t_ms,animId,actorHex(0x000..0xE00)");
            this.logStartMs = Environment.TickCount64;
        }

        private void StopLog()
        {
            if (this.csv != null)
            {
                this.csv.Flush();
                this.csv.Dispose();
                this.csv = null;
            }
        }

        private static string ToHex(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
                return string.Empty;
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes)
                sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        // Reads one stat layer of the Stats component into {statId -> value}.
        private static Dictionary<int, int> ReadStatLayer(IntPtr statsComp, int layerOffset)
        {
            var result = new Dictionary<int, int>();
            if (statsComp == IntPtr.Zero)
            {
                return result;
            }

            var layer = Mem.Ptr(statsComp + layerOffset);
            if (layer == IntPtr.Zero)
            {
                return result;
            }

            var first = Mem.Ptr(layer + StatsVectorInStruct);
            var last = Mem.Ptr(layer + StatsVectorInStruct + 8);
            if (first == IntPtr.Zero || last == IntPtr.Zero)
            {
                return result;
            }

            long count = (last.ToInt64() - first.ToInt64()) / 8;
            if (count <= 0 || count > 8000)
            {
                return result;
            }

            var bytes = Mem.ReadBytes(first, (int)count * 8);
            for (int i = 0; i + 8 <= bytes.Length; i += 8)
            {
                int key = BitConverter.ToInt32(bytes, i);
                int val = BitConverter.ToInt32(bytes, i + 4);
                result[key] = val;
            }

            return result;
        }

        private static IntPtr ResolveComponent(IntPtr entity, string name)
        {
            if (entity == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            var details = Mem.Ptr(entity + EntityDetailsOffset);
            if (details == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            var lookup = Mem.Ptr(details + 0x28);
            if (lookup == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            var clFirst = Mem.I64(entity + 0x10);
            var clLast = Mem.I64(entity + 0x18);
            var compCount = (clLast - clFirst) / 8;
            if (compCount <= 0 || compCount > 256)
            {
                return IntPtr.Zero;
            }

            var bFirst = Mem.I64(lookup + 0x28);
            var bLast = Mem.I64(lookup + 0x30);
            if (bFirst == 0 || bLast == 0)
            {
                return IntPtr.Zero;
            }

            var entries = (bLast - bFirst) / 16;
            if (entries <= 0 || entries > 256)
            {
                return IntPtr.Zero;
            }

            for (long i = 0; i < entries; i++)
            {
                var e = (IntPtr)(bFirst + (i * 16));
                var namePtr = Mem.I64(e);
                var index = Mem.I32(e + 8);
                if (index < 0 || index >= compCount || namePtr == 0)
                {
                    continue;
                }

                if (Mem.ReadAscii((IntPtr)namePtr, 64) != name)
                {
                    continue;
                }

                return Mem.Ptr((IntPtr)(clFirst + (index * 8)));
            }

            return IntPtr.Zero;
        }
    }
}
