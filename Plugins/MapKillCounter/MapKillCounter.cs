// <copyright file="MapKillCounter.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace MapKillCounter
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Numerics;
    using ExileBridge;
    using ImGuiNET;
    using Newtonsoft.Json;

    /// <summary>
    ///     Per-map and per-session monster kill counts by rarity (N/M/R/U), with map and
    ///     session timers and optional overlays. A read-only ExileBridge port of
    ///     MordWraith's MapKillCounter — original written for GameHelper2's legacy API.
    /// </summary>
    public sealed class MapKillCounter : Plugin<MapKillCounterSettings>
    {
        private readonly int[] killCounts = new int[4];
        private readonly int[] sessionKillCounts = new int[4];
        private readonly Dictionary<uint, MonsterTrack> trackedMonsters = new();
        private readonly Stopwatch mapTimer = new();
        private readonly Stopwatch sessionTimer = new();

        private readonly Dictionary<string, int> currencyCollected = new();
        private readonly HashSet<uint> seenLootIds = new();
        private readonly Stopwatch currencyPollClock = Stopwatch.StartNew();
        private readonly List<MapRecord> history = new();
        private int currencyCollectedTotal;

        private bool timerRunning;
        private bool sessionTimerRunning;

        private string currentAreaName = string.Empty;
        private string sessionMapAreaName = string.Empty;
        private string sessionMapAreaHash = string.Empty;
        private bool inSanctuary;
        private bool areaChangePending;
        private IDisposable? areaChangeToken;

        private string SettingsPath => Path.Combine(this.DirectoryPath, "config", "settings.json");

        private string HistoryPath => Path.Combine(this.DirectoryPath, "config", "history.json");

        /// <inheritdoc />
        public override void OnEnable(bool isGameAttached)
        {
            if (File.Exists(this.SettingsPath))
            {
                try
                {
                    var json = File.ReadAllText(this.SettingsPath);
                    this.Settings = JsonConvert.DeserializeObject<MapKillCounterSettings>(json) ?? new MapKillCounterSettings();
                }
                catch
                {
                    this.Settings = new MapKillCounterSettings();
                }
            }

            this.LoadHistory();
            this.areaChangeToken = this.Ctx.Events.OnAreaChange(() => this.areaChangePending = true);
            this.ResetSessionTotals();
            this.sessionTimer.Restart();
            this.sessionTimerRunning = true;

            if (this.Settings.ResetOnlyOnNewMap)
            {
                this.areaChangePending = true;
            }
            else
            {
                this.ResetMapStats();
                this.mapTimer.Start();
                this.timerRunning = true;
            }
        }

        /// <inheritdoc />
        public override void OnDisable()
        {
            this.areaChangeToken?.Dispose();
            this.areaChangeToken = null;
            this.mapTimer.Stop();
            this.sessionTimer.Stop();
            this.timerRunning = false;
            this.sessionTimerRunning = false;
        }

        /// <inheritdoc />
        public override void SaveSettings()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(this.SettingsPath)!);
            File.WriteAllText(this.SettingsPath, JsonConvert.SerializeObject(this.Settings, Formatting.Indented));
        }

        /// <inheritdoc />
        public override void DrawSettings()
        {
            ImGui.Checkbox("Show map overlay window", ref this.Settings.ShowOverlay);

            var overlayModeIndex = (int)this.Settings.OverlayMode;
            if (ImGui.Combo("Map overlay mode", ref overlayModeIndex, "Full (kills + time)\0Timer only (minimal)\0Chip (compact)\0"))
            {
                this.Settings.OverlayMode = (MapOverlayMode)overlayModeIndex;
            }

            ImGui.Checkbox("Show session overlay window", ref this.Settings.ShowSessionOverlay);
            ImGui.Checkbox("Show loot line in the chip", ref this.Settings.ShowCurrencyPill);
            ImGui.Checkbox("Pause timer in town / hideout", ref this.Settings.PauseTimerInTownOrHideout);
            ImGui.Checkbox("Hide overlay when game in background", ref this.Settings.HideOverlayWhenGameInBackground);
            ImGui.Checkbox("Pause timer when game in background", ref this.Settings.PauseTimerWhenGameInBackground);
            ImGui.Checkbox("Pause timer when game is paused (ESC)", ref this.Settings.PauseTimerWhenGamePaused);
            ImGui.Checkbox("Count kills in town / hideout", ref this.Settings.CountKillsInTownOrHideout);
            ImGui.Checkbox("Reset only on new map (keep stats in town / hideout)", ref this.Settings.ResetOnlyOnNewMap);

            ImGui.Separator();
            var layoutIndex = (int)this.Settings.Layout;
            if (ImGui.Combo("Kill list layout", ref layoutIndex, "Vertical\0Horizontal\0"))
            {
                this.Settings.Layout = (KillListLayout)layoutIndex;
            }

            ImGui.DragFloat("Overlay font scale", ref this.Settings.OverlayFontScale, 0.02f, 0.7f, 1.5f, "%.2f");

            if (this.Settings.OverlaySize.X < 1f || this.Settings.OverlaySize.Y < 1f)
            {
                this.Settings.OverlaySize = this.GetDefaultOverlaySize();
            }

            if (this.Settings.SessionOverlaySize.X < 1f || this.Settings.SessionOverlaySize.Y < 1f)
            {
                this.Settings.SessionOverlaySize = this.GetDefaultOverlaySize();
            }

            ImGui.DragFloat2("Map window size (px)", ref this.Settings.OverlaySize, 1f, 120f, 900f, "%.0f");
            if (ImGui.Button("Reset map window size"))
            {
                this.Settings.OverlaySize = this.GetDefaultOverlaySize();
            }

            ImGui.DragFloat2("Session window size (px)", ref this.Settings.SessionOverlaySize, 1f, 120f, 900f, "%.0f");
            if (ImGui.Button("Reset session window size"))
            {
                this.Settings.SessionOverlaySize = this.GetDefaultOverlaySize();
            }

            ImGui.Separator();
            ImGui.ColorEdit4("Window background", ref this.Settings.BackgroundColor);
            ImGui.ColorEdit4("Text color", ref this.Settings.TextColor);
            ImGui.ColorEdit4("Normal color", ref this.Settings.NormalColor);
            ImGui.ColorEdit4("Magic color", ref this.Settings.MagicColor);
            ImGui.ColorEdit4("Rare color", ref this.Settings.RareColor);
            ImGui.ColorEdit4("Unique color", ref this.Settings.UniqueColor);

            ImGui.Separator();
            if (ImGui.Button("Reset current map stats"))
            {
                this.ResetMapStats(keepAreaName: true);
            }

            ImGui.SameLine();
            if (ImGui.Button("Reset session stats"))
            {
                this.ResetSessionTotals();
                this.sessionTimer.Restart();
                this.sessionTimerRunning = true;
            }

            ImGui.Separator();
            this.DrawHistoryUi();
        }

        /// <inheritdoc />
        public override void DrawUI()
        {
            var game = this.Ctx.Game;
            if (game.State is not (GameState.InGame or GameState.Escape))
            {
                this.StopTimer();
                this.StopSessionTimer();
                return;
            }

            var isGamePaused = game.State == GameState.Escape;
            var ig = game.InGame;
            var isTownOrHideout = ig.IsTown || ig.IsHideout;

            if (this.areaChangePending)
            {
                this.areaChangePending = false;
                this.HandleAreaTransition(ig.AreaName, ig.AreaHash, isTownOrHideout);
            }

            this.UpdateAreaName(ig.AreaName, isTownOrHideout);
            this.UpdateTimer(isTownOrHideout, isGamePaused);
            this.UpdateSessionTimer();

            if (!isGamePaused && (!isTownOrHideout || this.Settings.CountKillsInTownOrHideout))
            {
                this.ProcessKills();
            }

            this.UpdateCurrency();

            if (this.Settings.ShowOverlay)
            {
                this.DrawMapOverlay(isTownOrHideout, isGamePaused);
            }

            if (this.Settings.ShowSessionOverlay)
            {
                this.DrawSessionOverlay();
            }
        }

        private void UpdateAreaName(string name, bool isTownOrHideout)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            if (this.Settings.ResetOnlyOnNewMap && isTownOrHideout && !string.IsNullOrWhiteSpace(this.sessionMapAreaName))
            {
                this.currentAreaName = this.sessionMapAreaName;
                return;
            }

            this.currentAreaName = name;
        }

        private void UpdateTimer(bool isTownOrHideout, bool isGamePaused)
        {
            if (!this.ShouldTimerRun(isTownOrHideout, isGamePaused))
            {
                this.StopTimer();
                return;
            }

            if (!this.timerRunning)
            {
                this.mapTimer.Start();
                this.timerRunning = true;
            }
        }

        private bool ShouldTimerRun(bool isTownOrHideout, bool isGamePaused)
        {
            if (this.Settings.PauseTimerWhenGamePaused && isGamePaused)
            {
                return false;
            }

            if (this.Settings.PauseTimerInTownOrHideout && isTownOrHideout)
            {
                return false;
            }

            if (this.Settings.PauseTimerWhenGameInBackground && !this.Ctx.Game.IsForeground)
            {
                return false;
            }

            return true;
        }

        private void StopTimer()
        {
            if (!this.timerRunning)
            {
                return;
            }

            this.mapTimer.Stop();
            this.timerRunning = false;
        }

        private void UpdateSessionTimer()
        {
            if (!this.sessionTimerRunning)
            {
                this.sessionTimer.Start();
                this.sessionTimerRunning = true;
            }
        }

        private void StopSessionTimer()
        {
            if (!this.sessionTimerRunning)
            {
                return;
            }

            this.sessionTimer.Stop();
            this.sessionTimerRunning = false;
        }

        private bool IsTimerPaused(bool isTownOrHideout, bool isGamePaused) =>
            !this.ShouldTimerRun(isTownOrHideout, isGamePaused);

        private void ProcessKills()
        {
            foreach (var entity in this.Ctx.Entities.Awake)
            {
                if (!this.IsCountableMonster(entity))
                {
                    continue;
                }

                if (!entity.TryGetComponent<IObjectMagicProperties>(out var omp))
                {
                    continue;
                }

                var rarity = omp.Rarity;
                if ((int)rarity < (int)Rarity.Normal || (int)rarity > (int)Rarity.Unique)
                {
                    continue;
                }

                var id = entity.Id;
                var isAlive = this.IsAliveMonster(entity);
                var isDead = !isAlive || entity.State == EntityState.Useless;

                if (!this.trackedMonsters.TryGetValue(id, out var track))
                {
                    this.trackedMonsters[id] = new MonsterTrack
                    {
                        Rarity = rarity,
                        WasAlive = isAlive,
                        Counted = isDead,
                    };
                    continue;
                }

                if (!track.Counted && track.WasAlive && isDead)
                {
                    this.killCounts[(int)rarity]++;
                    this.sessionKillCounts[(int)rarity]++;
                    track.Counted = true;
                }
                else if (isAlive)
                {
                    track.WasAlive = true;
                }

                track.Rarity = rarity;
                this.trackedMonsters[id] = track;
            }

            if (this.trackedMonsters.Count > 2500)
            {
                this.PruneTrackedMonsters();
            }
        }

        private void PruneTrackedMonsters()
        {
            var aliveIds = new HashSet<uint>();
            foreach (var entity in this.Ctx.Entities.Awake)
            {
                aliveIds.Add(entity.Id);
            }

            var stale = new List<uint>();
            foreach (var id in this.trackedMonsters.Keys)
            {
                if (!aliveIds.Contains(id))
                {
                    stale.Add(id);
                }
            }

            foreach (var id in stale)
            {
                this.trackedMonsters.Remove(id);
            }
        }

        private void DrawMapOverlay(bool isTownOrHideout, bool isGamePaused)
        {
            if (this.Settings.OverlayMode == MapOverlayMode.TimerOnly)
            {
                this.DrawTimerOnlyOverlay(isTownOrHideout, isGamePaused);
                return;
            }

            if (this.Settings.OverlayMode == MapOverlayMode.Chip)
            {
                this.DrawChipOverlay(isTownOrHideout, isGamePaused);
                return;
            }

            var areaLabel = string.IsNullOrWhiteSpace(this.currentAreaName) ? "Unknown area" : this.currentAreaName;
            var subtitle = isTownOrHideout && this.Settings.ResetOnlyOnNewMap && !string.IsNullOrWhiteSpace(this.sessionMapAreaName)
                ? "In town / hideout"
                : null;

            this.DrawStatsOverlay(
                windowId: "###MapKillCounterOverlay",
                title: "Map kills",
                ref this.Settings.OverlayPosition,
                this.Settings.OverlaySize,
                this.killCounts,
                this.mapTimer.Elapsed,
                areaLabel,
                subtitle,
                this.IsTimerPaused(isTownOrHideout, isGamePaused));
        }

        private void DrawSessionOverlay()
        {
            this.DrawStatsOverlay(
                windowId: "###MapKillCounterSessionOverlay",
                title: "Session kills",
                ref this.Settings.SessionOverlayPosition,
                this.Settings.SessionOverlaySize,
                this.sessionKillCounts,
                this.sessionTimer.Elapsed,
                "Whole session",
                null,
                showPaused: false);
        }

        private void DrawTimerOnlyOverlay(bool isTownOrHideout, bool isGamePaused)
        {
            if (!this.Ctx.Game.IsForeground && this.Settings.HideOverlayWhenGameInBackground)
            {
                return;
            }

            if (this.Ctx.Ui.IsAnyLargePanelOpen)
            {
                return;
            }

            var isPaused = this.IsTimerPaused(isTownOrHideout, isGamePaused);
            var timeText = this.FormatElapsed(this.mapTimer.Elapsed);
            if (isPaused)
            {
                timeText += " *";
            }

            if (this.Settings.OverlayPosition == new Vector2(40f, 120f))
            {
                var display = ImGui.GetIO().DisplaySize;
                ImGui.SetNextWindowPos(new Vector2(display.X - 72f, 12f), ImGuiCond.FirstUseEver);
            }
            else
            {
                ImGui.SetNextWindowPos(this.Settings.OverlayPosition, ImGuiCond.FirstUseEver);
            }

            ImGui.SetNextWindowBgAlpha(this.Settings.BackgroundColor.W);
            ImGui.PushStyleColor(ImGuiCol.WindowBg, this.Settings.BackgroundColor);
            ImGui.PushStyleColor(ImGuiCol.Text, this.Settings.TextColor);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(6f, 3f));
            ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);

            var flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoScrollbar |
                        ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.AlwaysAutoResize |
                        ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav;

            if (!ImGui.Begin("###MapKillCounterTimerOnly", flags))
            {
                ImGui.PopStyleVar(2);
                ImGui.PopStyleColor(2);
                ImGui.End();
                return;
            }

            var fontScale = Math.Clamp(this.Settings.OverlayFontScale, 0.7f, 1.5f);
            ImGui.SetWindowFontScale(fontScale);
            this.Settings.OverlayPosition = ImGui.GetWindowPos();
            ImGui.TextUnformatted(timeText);
            ImGui.SetWindowFontScale(1f);
            ImGui.End();
            ImGui.PopStyleVar(2);
            ImGui.PopStyleColor(2);
        }

        /// <summary>
        ///     Draws the compact "chip" overlay: a gold-bordered dark pill with the total
        ///     kill count, the map timer, and a live kills/hour figure.
        /// </summary>
        private void DrawChipOverlay(bool isTownOrHideout, bool isGamePaused)
        {
            if (!this.Ctx.Game.IsForeground && this.Settings.HideOverlayWhenGameInBackground)
            {
                return;
            }

            if (this.Ctx.Ui.IsAnyLargePanelOpen)
            {
                return;
            }

            var total = this.killCounts[0] + this.killCounts[1] + this.killCounts[2] + this.killCounts[3];
            var elapsed = this.mapTimer.Elapsed;
            var perHour = elapsed.TotalHours > 0.0007 ? (int)Math.Round(total / elapsed.TotalHours) : 0;
            var paused = this.IsTimerPaused(isTownOrHideout, isGamePaused);

            if (this.Settings.OverlayPosition == new Vector2(40f, 120f))
            {
                var display = ImGui.GetIO().DisplaySize;
                ImGui.SetNextWindowPos(new Vector2(display.X - 220f, 60f), ImGuiCond.FirstUseEver);
            }
            else
            {
                ImGui.SetNextWindowPos(this.Settings.OverlayPosition, ImGuiCond.FirstUseEver);
            }

            ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0.039f, 0.051f, 0.063f, 0.86f));
            ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(0.78f, 0.565f, 0.19f, 0.55f));
            ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 9f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1.5f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(15f, 9f));

            var flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoScrollbar |
                        ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.AlwaysAutoResize |
                        ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav;

            if (!ImGui.Begin("###MapKillCounterChip", flags))
            {
                ImGui.PopStyleVar(3);
                ImGui.PopStyleColor(2);
                ImGui.End();
                return;
            }

            var fontScale = Math.Clamp(this.Settings.OverlayFontScale, 0.7f, 1.5f);
            ImGui.SetWindowFontScale(fontScale * 1.1f);
            this.Settings.OverlayPosition = ImGui.GetWindowPos();

            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.84f, 0.66f, 0.30f, 1f));
            ImGui.TextUnformatted("KILLS");
            ImGui.PopStyleColor();

            ImGui.SameLine(0f, 8f);
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.95f, 0.93f, 0.84f, 1f));
            ImGui.TextUnformatted(total.ToString("N0"));
            ImGui.PopStyleColor();

            var dl = ImGui.GetWindowDrawList();
            ImGui.SameLine(0f, 16f);
            this.DrawRarityGem(dl, this.killCounts[(int)Rarity.Normal], this.Settings.NormalColor);
            ImGui.SameLine(0f, 11f);
            this.DrawRarityGem(dl, this.killCounts[(int)Rarity.Magic], this.Settings.MagicColor);
            ImGui.SameLine(0f, 11f);
            this.DrawRarityGem(dl, this.killCounts[(int)Rarity.Rare], this.Settings.RareColor);
            ImGui.SameLine(0f, 11f);
            this.DrawRarityGem(dl, this.killCounts[(int)Rarity.Unique], this.Settings.UniqueColor);

            ImGui.SameLine(0f, 16f);
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.62f, 0.71f, 0.81f, 1f));
            ImGui.TextUnformatted(paused ? "paused" : $"{perHour:N0} / hr");
            ImGui.PopStyleColor();

            // Second line: loot (currency dropped this map). Hover the pill for the breakdown.
            if (this.Settings.ShowCurrencyPill)
            {
                var gold = new Vector4(0.84f, 0.66f, 0.30f, 1f);
                ImGui.PushStyleColor(ImGuiCol.Text, gold);
                ImGui.TextUnformatted("LOOT ");
                ImGui.PopStyleColor();
                ImGui.SameLine(0f, 8f);
                this.DrawRarityGem(dl, this.currencyCollectedTotal, gold);
                ImGui.SameLine(0f, 16f);
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.62f, 0.71f, 0.81f, 1f));
                ImGui.TextUnformatted(this.currencyCollected.Count == 1 ? "1 type" : $"{this.currencyCollected.Count} types");
                ImGui.PopStyleColor();

                if (ImGui.IsWindowHovered() && this.currencyCollected.Count > 0)
                {
                    ImGui.BeginTooltip();
                    foreach (var kv in this.currencyCollected.OrderByDescending(k => k.Value))
                    {
                        ImGui.TextUnformatted($"{kv.Value,5}  {kv.Key}");
                    }

                    ImGui.EndTooltip();
                }
            }

            ImGui.SetWindowFontScale(1f);
            ImGui.End();
            ImGui.PopStyleVar(3);
            ImGui.PopStyleColor(2);
        }

        /// <summary>Draws a small rarity gem icon (a colored diamond) followed by its kill count, inline.</summary>
        private void DrawRarityGem(ImDrawListPtr dl, int count, Vector4 color)
        {
            var h = ImGui.GetTextLineHeight();
            var half = h * 0.30f;
            var p = ImGui.GetCursorScreenPos();
            var cx = p.X + half + 1f;
            var cy = p.Y + (h * 0.5f);
            var col = ImGui.GetColorU32(color);
            dl.AddQuadFilled(
                new Vector2(cx, cy - half), new Vector2(cx + half, cy),
                new Vector2(cx, cy + half), new Vector2(cx - half, cy), col);
            dl.AddQuad(
                new Vector2(cx, cy - half), new Vector2(cx + half, cy),
                new Vector2(cx, cy + half), new Vector2(cx - half, cy),
                ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.35f)), 1f);

            ImGui.Dummy(new Vector2((half * 2f) + 2f, h));
            ImGui.SameLine(0f, 4f);
            ImGui.PushStyleColor(ImGuiCol.Text, color);
            ImGui.TextUnformatted(count.ToString("N0"));
            ImGui.PopStyleColor();
        }

        /// <summary>
        ///     Scans nearby ground loot for currency drops and tallies each distinct drop once
        ///     (by entity id). Counts what the map dropped near you, independent of pickups.
        /// </summary>
        private void UpdateCurrency()
        {
            if (this.currencyPollClock.ElapsedMilliseconds < 250)
            {
                return;
            }

            this.currencyPollClock.Restart();

            foreach (var entity in this.Ctx.Entities.Awake)
            {
                if (entity.Type != EntityType.Item)
                {
                    continue;
                }

                if (!entity.TryGetComponent<IGroundItem>(out var ground) || ground == null)
                {
                    continue;
                }

                var path = ground.ItemPath;
                if (string.IsNullOrEmpty(path) ||
                    !path.StartsWith("Metadata/Items/Currency", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!this.seenLootIds.Add(entity.Id))
                {
                    continue;
                }

                var count = ground.StackCount > 0 ? ground.StackCount : 1;
                var name = !string.IsNullOrEmpty(ground.DisplayName) ? ground.DisplayName : PrettyPathName(path);
                this.currencyCollected[name] = this.currencyCollected.TryGetValue(name, out var v) ? v + count : count;
                this.currencyCollectedTotal += count;
            }
        }

        private static string PrettyPathName(string path)
        {
            var slash = path.LastIndexOf('/');
            return slash >= 0 && slash < path.Length - 1 ? path[(slash + 1)..] : path;
        }

        /// <summary>Appends the just-finished map to the on-disk history (skips trivial / town runs).</summary>
        private void RecordMapToHistory(string area)
        {
            if (string.IsNullOrWhiteSpace(area))
            {
                return;
            }

            var total = this.killCounts[0] + this.killCounts[1] + this.killCounts[2] + this.killCounts[3];
            if (total == 0 && this.currencyCollectedTotal == 0 && this.mapTimer.Elapsed.TotalSeconds < 15)
            {
                return;
            }

            var record = new MapRecord
            {
                TimestampUtc = DateTime.UtcNow.ToString("o"),
                Area = area,
                DurationSeconds = Math.Round(this.mapTimer.Elapsed.TotalSeconds, 1),
                KillsNormal = this.killCounts[0],
                KillsMagic = this.killCounts[1],
                KillsRare = this.killCounts[2],
                KillsUnique = this.killCounts[3],
                KillsTotal = total,
                Currency = new Dictionary<string, int>(this.currencyCollected),
                CurrencyTotal = this.currencyCollectedTotal,
            };

            this.history.Add(record);
            if (this.history.Count > 500)
            {
                this.history.RemoveRange(0, this.history.Count - 500);
            }

            this.SaveHistory();
        }

        private void LoadHistory()
        {
            try
            {
                if (File.Exists(this.HistoryPath))
                {
                    var loaded = JsonConvert.DeserializeObject<List<MapRecord>>(File.ReadAllText(this.HistoryPath));
                    this.history.Clear();
                    if (loaded != null)
                    {
                        this.history.AddRange(loaded);
                    }
                }
            }
            catch
            {
                // A corrupt or missing history file is non-fatal.
            }
        }

        private void SaveHistory()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(this.HistoryPath)!);
                File.WriteAllText(this.HistoryPath, JsonConvert.SerializeObject(this.history, Formatting.Indented));
            }
            catch
            {
                // A disk error is non-fatal.
            }
        }

        /// <summary>Draws the per-map history table in the settings page.</summary>
        private void DrawHistoryUi()
        {
            ImGui.Text($"Map history ({this.history.Count})");
            ImGui.SameLine();
            if (ImGui.SmallButton("Clear history"))
            {
                this.history.Clear();
                this.SaveHistory();
            }

            if (this.history.Count == 0)
            {
                ImGui.TextDisabled("No maps recorded yet. Finish a map to log it here.");
                return;
            }

            if (!ImGui.BeginTable(
                    "mapHistoryTable",
                    5,
                    ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.ScrollY,
                    new Vector2(0f, 240f)))
            {
                return;
            }

            ImGui.TableSetupColumn("When", ImGuiTableColumnFlags.WidthFixed, 110f);
            ImGui.TableSetupColumn("Area", ImGuiTableColumnFlags.WidthStretch, 0.5f);
            ImGui.TableSetupColumn("Time", ImGuiTableColumnFlags.WidthFixed, 60f);
            ImGui.TableSetupColumn("Kills", ImGuiTableColumnFlags.WidthFixed, 55f);
            ImGui.TableSetupColumn("Currency", ImGuiTableColumnFlags.WidthFixed, 70f);
            ImGui.TableHeadersRow();

            for (var i = this.history.Count - 1; i >= 0; i--)
            {
                var r = this.history[i];
                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted(FormatWhen(r.TimestampUtc));

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(r.Area);
                if (r.Currency.Count > 0 && ImGui.IsItemHovered())
                {
                    ImGui.BeginTooltip();
                    foreach (var kv in r.Currency.OrderByDescending(k => k.Value))
                    {
                        ImGui.TextUnformatted($"{kv.Value,5}  {kv.Key}");
                    }

                    ImGui.EndTooltip();
                }

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(FormatSeconds(r.DurationSeconds));

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(r.KillsTotal.ToString());

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(r.CurrencyTotal.ToString());
            }

            ImGui.EndTable();
        }

        private static string FormatWhen(string iso)
        {
            return DateTime.TryParse(iso, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt)
                ? dt.ToLocalTime().ToString("MM-dd HH:mm")
                : iso;
        }

        private static string FormatSeconds(double seconds)
        {
            var ts = TimeSpan.FromSeconds(seconds);
            return ts.TotalHours >= 1
                ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
                : $"{ts.Minutes:D2}:{ts.Seconds:D2}";
        }

        private void DrawStatsOverlay(
            string windowId,
            string title,
            ref Vector2 windowPosition,
            Vector2 configuredSize,
            int[] killCounts,
            TimeSpan elapsed,
            string areaLabel,
            string? subtitle,
            bool showPaused)
        {
            if (!this.Ctx.Game.IsForeground && this.Settings.HideOverlayWhenGameInBackground)
            {
                return;
            }

            if (this.Ctx.Ui.IsAnyLargePanelOpen)
            {
                return;
            }

            var overlaySize = configuredSize;
            if (overlaySize.X < 1f || overlaySize.Y < 1f)
            {
                overlaySize = this.GetDefaultOverlaySize();
            }

            ImGui.SetNextWindowPos(windowPosition, ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowSize(overlaySize, ImGuiCond.Always);
            ImGui.SetNextWindowBgAlpha(this.Settings.BackgroundColor.W);
            ImGui.PushStyleColor(ImGuiCol.WindowBg, this.Settings.BackgroundColor);
            ImGui.PushStyleColor(ImGuiCol.Text, this.Settings.TextColor);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(8f, 8f));
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8f, 4f));
            ImGui.PushStyleVar(ImGuiStyleVar.ItemInnerSpacing, new Vector2(4f, 4f));

            if (!ImGui.Begin($"{title}{windowId}",
                    ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoScrollbar |
                    ImGuiWindowFlags.NoScrollWithMouse))
            {
                ImGui.PopStyleVar(3);
                ImGui.PopStyleColor(2);
                ImGui.End();
                return;
            }

            var fontScale = Math.Clamp(this.Settings.OverlayFontScale, 0.7f, 1.5f);
            ImGui.SetWindowFontScale(fontScale);
            windowPosition = ImGui.GetWindowPos();

            ImGui.TextDisabled(areaLabel);
            if (!string.IsNullOrWhiteSpace(subtitle))
            {
                ImGui.TextDisabled(subtitle);
            }

            ImGui.Text($"Time: {this.FormatElapsed(elapsed)}");
            if (showPaused)
            {
                ImGui.SameLine();
                ImGui.TextDisabled("(paused)");
            }

            ImGui.Separator();
            this.DrawKillList(killCounts);
            var total = killCounts[0] + killCounts[1] + killCounts[2] + killCounts[3];
            ImGui.Separator();
            ImGui.Text($"Total: {total}");

            ImGui.SetWindowFontScale(1f);
            ImGui.End();
            ImGui.PopStyleVar(3);
            ImGui.PopStyleColor(2);
        }

        private void DrawKillList(int[] killCounts)
        {
            var horizontal = this.Settings.Layout == KillListLayout.Horizontal;
            this.DrawKillLine(killCounts, "N", Rarity.Normal, this.Settings.NormalColor, sameLine: false);
            this.DrawKillLine(killCounts, "M", Rarity.Magic, this.Settings.MagicColor, sameLine: horizontal);
            this.DrawKillLine(killCounts, "R", Rarity.Rare, this.Settings.RareColor, sameLine: horizontal);
            this.DrawKillLine(killCounts, "U", Rarity.Unique, this.Settings.UniqueColor, sameLine: horizontal);
        }

        private void DrawKillLine(int[] killCounts, string label, Rarity rarity, Vector4 color, bool sameLine)
        {
            if (sameLine)
            {
                ImGui.SameLine(0f, 14f);
            }

            ImGui.TextColored(color, $"{label}: {killCounts[(int)rarity]}");
        }

        private Vector2 GetDefaultOverlaySize() =>
            this.Settings.Layout == KillListLayout.Horizontal
                ? new Vector2(500f, 118f)
                : new Vector2(175f, 158f);

        private string FormatElapsed(TimeSpan elapsed)
        {
            if (elapsed.TotalHours >= 1)
            {
                return $"{(int)elapsed.TotalHours}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}";
            }

            return $"{elapsed.Minutes:D2}:{elapsed.Seconds:D2}";
        }

        private void ResetMapStats(bool keepAreaName = false)
        {
            Array.Clear(this.killCounts);
            this.trackedMonsters.Clear();
            this.mapTimer.Reset();
            this.currencyCollected.Clear();
            this.seenLootIds.Clear();
            this.currencyCollectedTotal = 0;
            if (!keepAreaName)
            {
                this.currentAreaName = string.Empty;
                this.sessionMapAreaName = string.Empty;
                this.sessionMapAreaHash = string.Empty;
            }
        }

        private void ResetSessionTotals()
        {
            Array.Clear(this.sessionKillCounts);
        }

        private void HandleAreaTransition(string areaName, string areaHash, bool isTownOrHideout)
        {
            if (!this.Settings.ResetOnlyOnNewMap)
            {
                if (!this.inSanctuary)
                {
                    this.RecordMapToHistory(this.currentAreaName);
                }

                this.inSanctuary = isTownOrHideout;
                this.ResetMapStats();
                if (!string.IsNullOrWhiteSpace(areaName))
                {
                    this.currentAreaName = areaName;
                }

                this.mapTimer.Restart();
                this.timerRunning = true;
                return;
            }

            if (isTownOrHideout)
            {
                this.inSanctuary = true;
                return;
            }

            var isNewMap = this.inSanctuary
                || string.IsNullOrEmpty(this.sessionMapAreaHash)
                || !string.Equals(this.sessionMapAreaHash, areaHash, StringComparison.OrdinalIgnoreCase);

            this.inSanctuary = false;

            if (!isNewMap)
            {
                return;
            }

            this.RecordMapToHistory(this.sessionMapAreaName);
            this.sessionMapAreaHash = areaHash;
            this.sessionMapAreaName = areaName;
            this.ResetMapStats(keepAreaName: true);
            this.currentAreaName = this.sessionMapAreaName;
            this.mapTimer.Restart();
            this.timerRunning = true;
        }

        private bool IsCountableMonster(IEntity entity)
        {
            if (!entity.IsValid)
            {
                return false;
            }

            if (entity.Type != EntityType.Monster)
            {
                return false;
            }

            if (entity.State is EntityState.MonsterFriendly or EntityState.PinnacleBossHidden)
            {
                return false;
            }

            return true;
        }

        private bool IsAliveMonster(IEntity entity) =>
            entity.TryGetComponent<ILife>(out var life) && life.IsAlive;

        private struct MonsterTrack
        {
            public Rarity Rarity;
            public bool WasAlive;
            public bool Counted;
        }
    }
}
