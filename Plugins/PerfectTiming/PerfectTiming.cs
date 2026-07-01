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
    ///     PerfectTiming — auto hold+release for the Warrior "Perfect Strike".
    ///
    ///     The SDK doesn't expose the Actor component, so this reads it via raw memory to get the live
    ///     AnimationId. Perfect Strike's charge shows as animation 461 (windup); the release outcome is
    ///     463 (PERFECT) or 462 (normal). There is no continuous charge value in the reachable memory,
    ///     so instead of reading a marker we anchor on the windup start (461) and release at a precise,
    ///     tuned delay — reading the 463/462 outcome as feedback to auto-tune toward perfect.
    /// </summary>
    public sealed class PerfectTiming : Plugin<PerfectTimingSettings>
    {
        // ActorOffset (from GameOffsets.Objects.Components.ActorOffset).
        private const int EntityDetailsOffset = 0x08;
        private const int ActorAnimationIdOffset = 0x8B0;
        private const int ActorActiveSkillsFirst = 0xB08; // StdVector: First @ +0, Last @ +8
        private const int ActiveSkillStride = 0x10;
        private const int SkillUseStageOffset = 0x08;
        private const int SkillCastTypeOffset = 0x0C;
        private const int SkillIdInfoOffset = 0x40;
        private const int SkillGrantedRowOffset = 0x48;
        private const int SkillStructBytes = 0x100;
        private const int ActorWindowStart = 0x880;
        private const int ActorWindowBytes = 0x80;

        // Auto-tune bounds (ms) — walk the delay down from ceil, wrap when it under-shoots the window.
        private const int TuneStep = 8;
        private const int TuneFloor = 380;
        private const int TuneCeil = 560;

        private enum Phase { Idle, WaitWindup, Charging, WaitOutcome }

        private Phase phase = Phase.Idle;
        private long pressTicks;
        private long outcomeStart;
        private volatile bool releaseScheduled;
        private volatile bool skillHeld;
        private bool triggerWasDown;
        private int consecMiss;

        private int hits;
        private int misses;
        private bool lastPerfect;
        private readonly Queue<bool> recent = new();

        private bool capturingTrigger;
        private bool capturingSkill;

        private StreamWriter csv;
        private long logStartMs;

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
        }

        public override void OnDisable()
        {
            this.ForceReleaseIfHeld();
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
                this.ForceReleaseIfHeld();
                this.phase = Phase.Idle;
                return;
            }

            Mem.Pid = this.Ctx.Game.Pid;
            var player = this.Ctx.Game.InGame.Player;
            if (player == null)
            {
                return;
            }

            var actor = ResolveComponent(player.Address, "Actor");
            int animId = actor != IntPtr.Zero ? Mem.I32(actor + ActorAnimationIdOffset) : int.MinValue;

            if (actor != IntPtr.Zero)
            {
                this.UpdateAuto(animId);
            }

            if (this.Settings.ShowProbe || this.csv != null)
            {
                var skills = actor != IntPtr.Zero ? this.ReadSkills(actor) : new List<SkillInfo>();
                SkillInfo? target = PickTarget(skills, this.Settings.TargetSkillMatch);
                if (this.csv != null && actor != IntPtr.Zero)
                {
                    this.LogFrame(actor, animId, target);
                }

                if (this.Settings.ShowProbe)
                {
                    this.DrawProbe(actor, animId, skills, target);
                }
            }
        }

        // ── Auto hold + release state machine ───────────────────────────────────────
        private void UpdateAuto(int animId)
        {
            bool triggerDown = this.Settings.TriggerKey != 0 && Input.IsKeyDown(this.Settings.TriggerKey);
            bool edge = triggerDown && !this.triggerWasDown;
            this.triggerWasDown = triggerDown;

            if (!this.Settings.AutoEnabled)
            {
                if (this.phase != Phase.Idle)
                {
                    this.ForceReleaseIfHeld();
                    this.phase = Phase.Idle;
                }

                return;
            }

            long now = Stopwatch.GetTimestamp();
            switch (this.phase)
            {
                case Phase.Idle:
                    if (triggerDown && (this.Settings.RepeatWhileHeld || edge))
                    {
                        this.SkillDown();
                        this.pressTicks = now;
                        this.phase = Phase.WaitWindup;
                    }

                    break;

                case Phase.WaitWindup:
                    if (animId == this.Settings.WindupAnim)
                    {
                        this.ScheduleRelease(this.Settings.ReleaseDelayMs);
                        this.phase = Phase.Charging;
                    }
                    else if (ElapsedMs(this.pressTicks, now) > 700)
                    {
                        // Windup never started (out of range / interrupted) — release and reset.
                        this.ForceReleaseIfHeld();
                        this.phase = Phase.Idle;
                    }

                    break;

                case Phase.Charging:
                    if (!this.releaseScheduled)
                    {
                        this.outcomeStart = now;
                        this.phase = Phase.WaitOutcome;
                    }

                    break;

                case Phase.WaitOutcome:
                    if (animId == this.Settings.PerfectAnim)
                    {
                        this.RecordOutcome(true);
                        this.EndCycle(triggerDown);
                    }
                    else if (animId == this.Settings.EndAnim)
                    {
                        this.RecordOutcome(false);
                        this.EndCycle(triggerDown);
                    }
                    else if (ElapsedMs(this.outcomeStart, now) > 500)
                    {
                        // No clear outcome (interrupted) — don't score it.
                        this.EndCycle(triggerDown);
                    }

                    break;
            }
        }

        private void EndCycle(bool triggerDown)
        {
            if (this.Settings.AutoEnabled && this.Settings.RepeatWhileHeld && triggerDown)
            {
                this.SkillDown();
                this.pressTicks = Stopwatch.GetTimestamp();
                this.phase = Phase.WaitWindup;
            }
            else
            {
                this.phase = Phase.Idle;
            }
        }

        private void RecordOutcome(bool perfect)
        {
            this.lastPerfect = perfect;
            if (perfect)
            {
                this.hits++;
            }
            else
            {
                this.misses++;
            }

            this.recent.Enqueue(perfect);
            while (this.recent.Count > 20)
            {
                this.recent.Dequeue();
            }

            if (this.Settings.AutoTune)
            {
                if (perfect)
                {
                    this.consecMiss = 0;
                }
                else if (++this.consecMiss >= 2)
                {
                    this.consecMiss = 0;
                    this.Settings.ReleaseDelayMs -= TuneStep;
                    if (this.Settings.ReleaseDelayMs < TuneFloor)
                    {
                        this.Settings.ReleaseDelayMs = TuneCeil;
                    }
                }
            }
        }

        // Precise release: wait exactly delayMs from windup-start, then send the skill key-up.
        private void ScheduleRelease(int delayMs)
        {
            this.releaseScheduled = true;
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

                    this.SkillUp();
                }
                finally
                {
                    this.releaseScheduled = false;
                }
            });
        }

        private void SkillDown()
        {
            if (this.skillHeld)
            {
                return;
            }

            this.skillHeld = true;
            if (this.Settings.SkillIsMouse)
            {
                Input.MouseDown((Input.MouseButton)Math.Clamp(this.Settings.SkillMouseButton, 0, 2));
            }
            else if (this.Settings.SkillKey != 0)
            {
                Input.KeyDown(this.Settings.SkillKey);
            }
        }

        private void SkillUp()
        {
            if (!this.skillHeld)
            {
                return;
            }

            this.skillHeld = false;
            if (this.Settings.SkillIsMouse)
            {
                Input.MouseUp((Input.MouseButton)Math.Clamp(this.Settings.SkillMouseButton, 0, 2));
            }
            else if (this.Settings.SkillKey != 0)
            {
                Input.KeyUp(this.Settings.SkillKey);
            }
        }

        private void ForceReleaseIfHeld()
        {
            if (this.skillHeld)
            {
                this.SkillUp();
            }
        }

        private static long ElapsedMs(long startTicks, long nowTicks)
            => (nowTicks - startTicks) * 1000 / Stopwatch.Frequency;

        // ── Settings UI ─────────────────────────────────────────────────────────────
        public override void DrawSettings()
        {
            ImGui.TextWrapped("Auto hold+release for the Warrior 'Perfect Strike'. Hold the trigger key; " +
                              "the plugin holds the skill, waits for the windup, and releases at the tuned time.");
            ImGui.Separator();

            ImGui.Checkbox("Enable auto Perfect Strike", ref this.Settings.AutoEnabled);

            ImGui.Text($"Trigger key: {KeyName(this.Settings.TriggerKey)}");
            ImGui.SameLine();
            if (ImGui.Button(this.capturingTrigger ? "press a key...##t" : "Set##trig"))
            {
                this.capturingTrigger = true;
            }

            if (this.capturingTrigger && Input.TryCaptureKey(out var tvk))
            {
                this.Settings.TriggerKey = tvk;
                this.capturingTrigger = false;
            }

            ImGui.Checkbox("Skill is a mouse button", ref this.Settings.SkillIsMouse);
            if (this.Settings.SkillIsMouse)
            {
                string[] mb = { "Left", "Right", "Middle" };
                ImGui.Combo("Mouse button", ref this.Settings.SkillMouseButton, mb, mb.Length);
            }
            else
            {
                ImGui.Text($"Skill key: {KeyName(this.Settings.SkillKey)}");
                ImGui.SameLine();
                if (ImGui.Button(this.capturingSkill ? "press a key...##s" : "Set##skill"))
                {
                    this.capturingSkill = true;
                }

                if (this.capturingSkill && Input.TryCaptureKey(out var svk))
                {
                    this.Settings.SkillKey = svk;
                    this.capturingSkill = false;
                }
            }

            ImGui.SliderInt("Release delay (ms)", ref this.Settings.ReleaseDelayMs, 200, 900);
            Draw.ToolTip("Time from windup start (anim 461) to release. Tune so the perfect rate below is highest.");
            ImGui.Checkbox("Repeat while trigger held", ref this.Settings.RepeatWhileHeld);
            ImGui.Checkbox("Auto-tune delay toward perfect", ref this.Settings.AutoTune);
            Draw.ToolTip("On repeated misses, walks the delay down (misses cluster at max windup) until perfect returns.");

            ImGui.Separator();
            int rec = this.recent.Count;
            int recHits = 0;
            foreach (var b in this.recent)
            {
                if (b) recHits++;
            }

            ImGui.Text($"Perfect: {this.hits}    Miss: {this.misses}    Last: {(this.lastPerfect ? "PERFECT" : "-")}");
            ImGui.Text($"Recent perfect rate: {(rec > 0 ? 100 * recHits / rec : 0)}%   (last {rec})");
            ImGui.Text($"Phase: {this.phase}    SkillHeld: {this.skillHeld}");
            if (ImGui.Button("Reset stats"))
            {
                this.hits = 0;
                this.misses = 0;
                this.recent.Clear();
            }

            ImGui.Separator();
            if (ImGui.CollapsingHeader("Probe / diagnostics"))
            {
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

        private static string KeyName(int vk) => vk == 0 ? "(none)" : $"0x{vk:X2} ({vk})";

        // ── Probe (kept for diagnostics) ────────────────────────────────────────────
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

        private void DrawProbe(IntPtr actor, int animId, List<SkillInfo> skills, SkillInfo? target)
        {
            ImGui.SetNextWindowBgAlpha(0.9f);
            if (ImGui.Begin("Perfect Strike Probe"))
            {
                if (actor == IntPtr.Zero)
                {
                    ImGui.Text("Actor component not found on player.");
                    ImGui.End();
                    return;
                }

                ImGui.Text($"AnimationId: {animId}  (0x{animId:X})");
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
                        ImGui.BeginTable("slots", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY, new Vector2(0f, 240f)))
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

        private void LogFrame(IntPtr actor, int animId, SkillInfo? target)
        {
            long t = Environment.TickCount64 - this.logStartMs;
            var skillHex = target.HasValue ? ToHex(Mem.ReadBytes(target.Value.Details, SkillStructBytes)) : string.Empty;
            var actorHex = ToHex(Mem.ReadBytes(actor + ActorWindowStart, ActorWindowBytes));
            var name = target.HasValue ? target.Value.Name : string.Empty;
            int useStage = target.HasValue ? target.Value.UseStage : 0;
            int castType = target.HasValue ? target.Value.CastType : 0;
            this.csv.WriteLine($"{t},{animId},{name},{useStage},{castType},{skillHex},{actorHex}");
        }

        private void StartLog()
        {
            var dir = Path.GetDirectoryName(this.LogPathname);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            this.csv = new StreamWriter(this.LogPathname, append: false) { AutoFlush = true };
            this.csv.WriteLine("t_ms,animId,skill,useStage,castType,skillHex,actorHex");
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

        // Resolve a named component off an entity via its component table (from the Economy ritual reader).
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
