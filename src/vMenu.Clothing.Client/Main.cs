using CitizenFX.FiveM.Client;
using CitizenFX.FiveM.Shared.Script;
using System.Globalization;
using System.Text.Json;
using vMenu.Enhanced.ClientAPI;

namespace vMenu.Clothing.Client
{
    public static class ThermalEvents
    {
        public const string RequestFullSync = "vMenu.Clothing:Thermal:RequestFullSync";
        public const string FullSync = "vMenu.Clothing:Thermal:FullSync";
        public const string SetValues = "vMenu.Clothing:Thermal:SetValues";
        public const string SetResult = "vMenu.Clothing:Thermal:SetResult";
        public const string ValueUpdated = "vMenu.Clothing:Thermal:ValueUpdated";
        public const string MoveRateChanged = "vMenu.Clothing:Thermal:MoveRateChanged";
        public const string MoveRateSync = "vMenu.Clothing:Thermal:MoveRateSync";   
    }

    public record WornThermal(
        string Model,
        string Type,
        int Id,
        string Name,
        string Dlc,
        int Local,
        float ThermalValue,
        bool Found)
    {
        public string Key => Main.Key(Model, Type, Id, Dlc, Local);
    }

    public class Main : IScript
    {
        private static readonly (float Temperature, float ThermalValue)[] Points =
        {
            (   Temperature: -273.15f,  ThermalValue: 5000.0f   ),
            (   Temperature: -50.0f,   ThermalValue: 400.0f    ),
            (   Temperature: -25.0f,   ThermalValue: 330.0f    ),
            (   Temperature: -10.0f,   ThermalValue: 200.0f    ),
            (   Temperature:   0.0f,   ThermalValue: 140.0f    ),
            (   Temperature:  18.0f,   ThermalValue:  85.0f    ),
            (   Temperature:  30.0f,   ThermalValue:  50.0f    ),
            (   Temperature:  45.0f,   ThermalValue:  15.0f    ),
            (   Temperature:  60.0f,   ThermalValue:   0.0f    ),
        };


        public const float NormalBodyTemp = 37.0f;
        public const float MaxTempSwing = 15.0f;
        public const float ApproachRate = 0.05f;

        public const float MinThermal = 0.0f;
        public const float MaxThermal = 100.0f;
        public const float ThermalStep = 0.5f;
        public const float DefaultThermal = 2.5f;

        private const int TickIntervalMs = 1000;
        private const int SendDelayMs = 500;

        public const int DamageMin = 1;
        public const int DamageMax = 2;

        public const bool UseTimecycle = true;
        public const string ColdTimecycle = "hud_def_desat_cold";
        public const string HotTimecycle = "REDMIST_blend";
        public const float TooTimecycleStrength = 0.4f;
        public const float WayTooTimecycleStrength = 1.0f;

        public const float TooHotStaminaDrainPerSec = 0.05f;
        public const float WayTooHotStaminaDrainPerSec = 0.15f;

        public const float TooColdMoveRate = 0.85f;
        public const float WayTooColdMoveRate = 0.65f;

        private static readonly uint MaleModel = API.Hash("mp_m_freemode_01");
        private static readonly uint FemaleModel = API.Hash("mp_f_freemode_01");
        private static readonly uint Franklin = API.Hash("player_one");
        private static readonly uint Trevor = API.Hash("player_two");
        private static readonly uint Micheal = API.Hash("player_zero");

        private static readonly Dictionary<uint, string> ModelNames = new()
        {
            [MaleModel] = "mp_m_freemode_01",
            [FemaleModel] = "mp_f_freemode_01",
            [Franklin] = "player_one",
            [Trevor] = "player_two",
            [Micheal] = "player_zero"
        };

        public static float BodyTemp = NormalBodyTemp;

        public enum BodyState
        {
            WayTooCold, TooCold, Normal, TooHot, WayTooHot
        }

        public static BodyState CurrentBodyState => BodyTemp switch
        {
            < 24.0f => BodyState.WayTooCold,  
            < 30.0f => BodyState.TooCold,     
            > 50.0f => BodyState.WayTooHot,   
            > 46.0f => BodyState.TooHot,      
            _ => BodyState.Normal
        };

        public static float CurrentTempHeight(float height)
            => Native.IsInteriorScene() ? 21.0f: API.Exports["vMenu.Enhanced"].Call<float>("GetTemperatureAtHeight", height);

        public static float GetRequiredThermal(float temperature)
        {
            if (temperature <= Points[0].Temperature)
                return Points[0].ThermalValue;

            for (int i = 0; i < Points.Length - 1; i++)
            {
                var a = Points[i];
                var b = Points[i + 1];
                if (temperature <= b.Temperature)
                {
                    float t = (temperature - a.Temperature) / (b.Temperature - a.Temperature);
                    return a.ThermalValue + t * (b.ThermalValue - a.ThermalValue);
                }
            }
            return Points[^1].ThermalValue;
        }

        private static readonly Dictionary<int, string> ComponentNames = new()
        {
            [0] = "Head",
            [1] = "Mask",
            [2] = "Hair",
            [3] = "Torso/Arms",
            [4] = "Legs",
            [5] = "Bags",
            [6] = "Shoes",
            [7] = "Accessories",
            [8] = "Undershirt",
            [9] = "Body Armor",
            [10] = "Decals",
            [11] = "Jackets/Tops"
        };

        private static readonly Dictionary<int, string> PropNames = new()
        {
            [0] = "Hats",
            [1] = "Glasses",
            [2] = "Ears",
            [6] = "Watches",
            [7] = "Bracelets"
        };

        private static Dictionary<string, float> _index = [];
        private static bool _synced;

        public static string Key(string model, string type, int id, string dlc, int local)
            => $"{model}|{type}|{id}|{(string.IsNullOrEmpty(dlc) ? "base game" : dlc)}|{local}";

        public static string ModelName(int ped)
        {
            uint hash = Native.GetEntityModel(ped);
            return ModelNames.TryGetValue(hash, out string? name) ? name : $"0x{hash:X8}";
        }

        private static float Lookup(string key, out bool found)
        {
            found = _index.TryGetValue(key, out float value);
            return found ? value : DefaultThermal;
        }

        public static (List<WornThermal> Items, float Total) GetWornThermal(int ped)
        {
            var list = new List<WornThermal>();
            float total = 0.0f;
            string model = ModelName(ped);

            foreach (var (id, name) in ComponentNames)
            {
                int drawable = Native.GetPedDrawableVariation(ped, id);
                string dlc = Native.GetPedCollectionNameFromDrawable(ped, id, drawable) ?? "";
                int local = Native.GetPedCollectionLocalIndexFromDrawable(ped, id, drawable);

                float t = Lookup(Key(model, "component", id, dlc, local), out bool found);
                total += t;
                list.Add(new WornThermal(model, "component", id, name, dlc, local, t, found));
            }

            foreach (var (id, name) in PropNames)
            {
                int prop = Native.GetPedPropIndex(ped, id, false);
                if (prop == -1) continue;

                string dlc = Native.GetPedCollectionNameFromProp(ped, id, prop) ?? "";
                int local = Native.GetPedCollectionLocalIndexFromProp(ped, id, prop);

                float t = Lookup(Key(model, "prop", id, dlc, local), out bool found);
                total += t;
                list.Add(new WornThermal(model, "prop", id, name, dlc, local, t, found));
            }

            return (list, total);
        }

        public static float GetCurrentThermalValue()
            => GetWornThermal(Native.PlayerPedId()).Total;

        private async Task BodyTempTicker()
        {
            int lastTime = Native.GetGameTimer();

            while (true)
            {
                await API.Delay(TickIntervalMs);

                int now = Native.GetGameTimer();
                float dt = (now - lastTime) / 1000f;
                lastTime = now;

                if (!_synced) continue;

                int ped = Native.PlayerPedId();
                uint model = Native.GetEntityModel(ped);
                if (model != MaleModel && model != FemaleModel) continue;

                if (Native.IsPedDeadOrDying(ped, true))
                {
                    BodyTemp = NormalBodyTemp;
                    continue;
                }

                float ambient = CurrentTempHeight(Native.GetEntityCoords(ped, false).Z);
                float clothing = GetCurrentThermalValue();
                float deficit = GetRequiredThermal(ambient) - clothing;
                float factor = Math.Clamp(deficit / 200f, -1f, 1f);
                float target = NormalBodyTemp - factor * MaxTempSwing;
                BodyTemp = target + (BodyTemp - target) * MathF.Exp(-ApproachRate * dt);

                API.Log.Debug($"[thermal] body {BodyTemp:F2} target {target:F2} {CurrentBodyState} clothing {clothing:F1}");

                if (CurrentBodyState is BodyState.WayTooCold or BodyState.WayTooHot)
                {
                    API.Players.Local.Ped!.Health -= Random.Shared.Next(DamageMin, DamageMax + 1);
                }
            }
        }

        private static string? _appliedTimecycle;
        private static float _appliedTimecycleStrength = -1f;
        private static float _sentMoveRate = 1f;
        private static readonly Dictionary<int, float> _remoteMoveRates = [];

        private static float GetMoveRate(BodyState s) => s switch
        {
            BodyState.WayTooCold => WayTooColdMoveRate,
            BodyState.TooCold => TooColdMoveRate,
            _ => 1f
        };

        private static string? GetTimecycle(BodyState s) => s switch
        {
            BodyState.TooCold or BodyState.WayTooCold => ColdTimecycle,
            BodyState.TooHot or BodyState.WayTooHot => HotTimecycle,
            _ => null
        };

        private static float GetTimecycleStrength(BodyState s) => s switch
        {
            BodyState.WayTooCold or BodyState.WayTooHot => WayTooTimecycleStrength,
            BodyState.TooCold or BodyState.TooHot => TooTimecycleStrength,
            _ => 0f
        };

        private static float GetStaminaDrainPerSec(BodyState s) => s switch
        {
            BodyState.WayTooHot => WayTooHotStaminaDrainPerSec,
            BodyState.TooHot => TooHotStaminaDrainPerSec,
            _ => 0f
        };

        private async Task EffectsTicker()
        {
            while (true)
            {
                await API.Delay(0); 

                int ped = Native.PlayerPedId();
                int player = Native.PlayerId();
                uint model = Native.GetEntityModel(ped);

                bool active = _synced
                    && (model == MaleModel || model == FemaleModel)
                    && !Native.IsPedDeadOrDying(ped, true);

                var state = active ? CurrentBodyState : BodyState.Normal;

                if (state is BodyState.WayTooCold or BodyState.WayTooHot)
                    Native.DisablePlayerHealthRecharge(player);

                float drain = GetStaminaDrainPerSec(state);
                if (drain > 0f && Native.IsPedSprinting(ped))
                    Native.RestorePlayerStamina(player, -drain * Native.GetFrameTime());

                float moveRate = GetMoveRate(state);
                if (moveRate != 1f)
                    Native.SetPedMoveRateOverride(ped, moveRate);

                if (moveRate != _sentMoveRate)
                {
                    if (moveRate == 1f) Native.SetPedMoveRateOverride(ped, 1f);
                    _sentMoveRate = moveRate;
                    API.EmitServer(ThermalEvents.MoveRateChanged, moveRate);
                }

                string? tc = UseTimecycle ? GetTimecycle(state) : null;
                if (tc != _appliedTimecycle)
                {
                    if (tc is null) Native.ClearTimecycleModifier();
                    else Native.SetTimecycleModifier(tc);
                    _appliedTimecycle = tc;
                    _appliedTimecycleStrength = -1f;
                }

                if (tc is not null)
                {
                    float strength = GetTimecycleStrength(state);
                    if (strength != _appliedTimecycleStrength)
                    {
                        Native.SetTimecycleModifierStrength(strength);
                        _appliedTimecycleStrength = strength;
                    }
                }

                foreach (var (serverId, rate) in _remoteMoveRates)
                {
                    int remote = Native.GetPlayerFromServerId(serverId);
                    if (remote == -1 || remote == player) continue;
                    int remotePed = Native.GetPlayerPed(remote);
                    if (Native.DoesEntityExist(remotePed))
                        Native.SetPedMoveRateOverride(remotePed, rate);
                }
            }
        }

        [OnNetEvent(ThermalEvents.MoveRateSync)]
        public void OnMoveRateSync(int serverId, float rate)
        {
            if (rate == 1f)
            {
                _remoteMoveRates.Remove(serverId);

                int remote = Native.GetPlayerFromServerId(serverId);
                if (remote != -1 && remote != Native.PlayerId())
                {
                    int remotePed = Native.GetPlayerPed(remote);
                    if (Native.DoesEntityExist(remotePed))
                        Native.SetPedMoveRateOverride(remotePed, 1f);
                }
            }
            else
            {
                _remoteMoveRates[serverId] = rate;
            }
        }

        private static readonly Text[] ThermalOptions = Enumerable
            .Range(0, (int)((MaxThermal - MinThermal) / ThermalStep) + 1)
            .Select(i => Text.Literal(FormatThermal(MinThermal + i * ThermalStep)))
            .ToArray();

        private static string FormatThermal(float value)
            => value.ToString("0.0", CultureInfo.InvariantCulture);

        private static int ToIndex(float value)
            => (int)MathF.Round((Math.Clamp(value, MinThermal, MaxThermal) - MinThermal) / ThermalStep);

        private static float FromIndex(int index)
            => MinThermal + index * ThermalStep;

        private static bool TryParseThermal(string? input, out float value)
        {
            value = 0f;
            if (string.IsNullOrWhiteSpace(input)) return false;
            if (!float.TryParse(input.Trim().Replace(',', '.'), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out value)) return false;
            if (value < MinThermal || value > MaxThermal) return false;
            return MathF.Abs(value / ThermalStep - MathF.Round(value / ThermalStep)) < 0.0001f;
        }

        private static VMenuPlugin? _plugin;
        private static PluginSubmenu? _wornSub;
        private static readonly Dictionary<string, (WornThermal Item, PluginList Row)> _rows = [];
        private static readonly Dictionary<string, CancellationTokenSource> _pendingSends = [];
        private static readonly Dictionary<int, string> _pendingResults = [];
        private static int _nextRequestId;

        public async void Initialize()
        {
            _ = BodyTempTicker();
            _ = EffectsTicker();

            API.Exports.Local.Set("GetBodyState", new Func<string?>(() => CurrentBodyState.ToString()));
            API.Exports.Local.Set("GetTotalThermalValue", new Func<float?>(() => GetCurrentThermalValue()));
            API.Exports.Local.Set("GetBodyTemperature", new Func<float?>(() => BodyTemp));

            API.EmitServer(ThermalEvents.RequestFullSync);

            _plugin = VMenuPlugin.Create("Thermal Clothing");
            _plugin.DescriptionKey = "thermal.description";
            _plugin.RootMenu.Subtitle = Text.Literal("Thermal Clothing Editor");
            AddThermalTranslations(_plugin);

            var enabled = _plugin.Settings.Bool(
                "Enabled", true,
                "Turns the thermal clothing editor on or off.");
            var gate = PluginGate.Permission("Edit") & PluginGate.Setting(enabled);

            _wornSub = _plugin.RootMenu.AddSubmenu(
                Text.Key("thermal.worn_list"),
                subtitle: Text.Literal("Thermal Editor"));
            _wornSub.Description = Text.Key("thermal.worn_list.desc");
            _wornSub.Gate = gate;
            _wornSub.Menu.Opened += RebuildWornMenu;

            var typeValue = _wornSub.Menu.AddKey(
                "type_value",
                Text.Key("thermal.type_value"),
                defaultKey: "X",
                defaultButton: "RLEFT_INDEX",
                description: Text.Key("thermal.type_value.desc"),
                shadowedControl: 73);
            typeValue.Pressed += async press => await TypeValueAsync(press.Item);

            var reload = _plugin.RootMenu.AddButton(Text.Key("thermal.reload"));
            reload.Description = Text.Key("thermal.reload.desc");
            reload.Gate = gate;
            reload.Selected += () =>
            {
                API.EmitServer(ThermalEvents.RequestFullSync);
                _plugin.Notify(NotifyStyle.Info, Text.Key("thermal.reloading"));
            };

            var totalBtn = _plugin.RootMenu.AddButton(Text.Key("thermal.show_total"));
            totalBtn.Description = Text.Key("thermal.show_total.desc");
            totalBtn.Selected += () =>
                _plugin.Notify(NotifyStyle.Info,
                    Text.Literal($"Current total thermal: {FormatThermal(GetCurrentThermalValue())}"));

            var result = await _plugin.ConnectAsync();
            API.Log.Info($"[thermal] Registered with vMenu: {result.Accepted}");
        }

        private static void RebuildWornMenu()
        {
            if (_plugin is null || _wornSub is null) return;

            var (items, total) = GetWornThermal(Native.PlayerPedId());
            var menu = _wornSub.Menu;
            _rows.Clear();

            using (_plugin.BeginBatch())
            {
                menu.Clear();
                menu.Subtitle = Text.Literal($"Total thermal: {FormatThermal(total)}");

                foreach (var item in items)
                {
                    string label = $"[{(item.Type == "prop" ? "P" : "C")}{item.Id}] {item.Name}";
                    var row = menu.AddList(Text.Literal(label), ThermalOptions, ToIndex(item.ThermalValue));
                    row.Description = BuildRowDescription(item);

                    string key = item.Key;
                    _rows[key] = (item, row);
                    row.IndexChanged += (_, newIndex) => QueueSend(key, FromIndex(newIndex));
                }

                var setAll = menu.AddButton(Text.Key("thermal.set_all"));
                setAll.Description = Text.Key("thermal.set_all.desc");
                setAll.Selected += async () => await SetAllAsync();
            }
        }

        private static async Task SetAllAsync()
        {
            if (_plugin is null || _rows.Count == 0) return;

            string? input = await _plugin.GetTextAsync(Text.Key("thermal.set_all.prompt"), maxLength: 5);
            if (string.IsNullOrWhiteSpace(input)) return;

            if (!TryParseThermal(input, out float value))
            {
                _plugin.Notify(NotifyStyle.Error, Text.Key("thermal.invalid"));
                return;
            }

            var items = _rows.Values.Select(r => r.Item).ToList();
            foreach (var item in items)
            {
                if (_pendingSends.Remove(item.Key, out var pending))
                    pending.Cancel();
                SetRow(item.Key, value, true);
            }

            Send(items, value, $"All {items.Count} worn pieces set to {FormatThermal(value)}.");
        }

        private static async Task TypeValueAsync(PluginItem? row)
        {
            if (_plugin is null) return;

            var match = _rows.FirstOrDefault(r => ReferenceEquals(r.Value.Row, row));
            if (match.Key is null) return;

            var item = match.Value.Item;
            string? input = await _plugin.GetTextAsync(
                Text.Literal($"Thermal value for {item.Name} (0 to 20, steps of 0.5)"),
                maxLength: 5,
                initialValue: FormatThermal(item.ThermalValue));
            if (string.IsNullOrWhiteSpace(input)) return;

            if (!TryParseThermal(input, out float value))
            {
                _plugin.Notify(NotifyStyle.Error, Text.Key("thermal.invalid"));
                return;
            }

            QueueSend(match.Key, value);
        }

        private static Text BuildRowDescription(WornThermal item)
        {
            string dlc = string.IsNullOrEmpty(item.Dlc) ? "base game" : item.Dlc;
            string status = item.Found ? "" : $"~n~Using the default of {FormatThermal(DefaultThermal)}, not saved yet.";
            return Text.Literal(
                $"{item.Name} ({item.Type} {item.Id})~n~DLC: {dlc}, local index: {item.Local}{status}~n~Press X to type a value.");
        }

        private static void SetRow(string key, float value, bool found)
        {
            if (found)
                _index[key] = value;

            if (!_rows.TryGetValue(key, out var entry)) return;

            var updated = entry.Item with { ThermalValue = value, Found = found };
            _rows[key] = (updated, entry.Row);
            entry.Row.Description = BuildRowDescription(updated);

            int index = ToIndex(value);
            if (entry.Row.SelectedIndex != index)
                entry.Row.SelectedIndex = index;

            if (_wornSub is not null)
                _wornSub.Menu.Subtitle = Text.Literal(
                    $"Total thermal: {FormatThermal(_rows.Values.Sum(r => r.Item.ThermalValue))}");
        }

        private static async void QueueSend(string key, float value)
        {
            if (!_rows.TryGetValue(key, out var entry)) return;
            if (entry.Item.Found && entry.Item.ThermalValue == value) return;

            if (_pendingSends.Remove(key, out var previous))
                previous.Cancel();

            var cts = new CancellationTokenSource();
            _pendingSends[key] = cts;
            var item = entry.Item;

            SetRow(key, value, true);

            await API.Delay(SendDelayMs);
            if (cts.IsCancellationRequested) return;
            _pendingSends.Remove(key);

            Send([item], value, $"{item.Name} set to {FormatThermal(value)}.");
        }

        private static void Send(List<WornThermal> items, float value, string successMessage)
        {
            int requestId = ++_nextRequestId;
            _pendingResults[requestId] = successMessage;

            string pieces = JsonSerializer.Serialize(items.Select(i => new { i.Type, i.Id, i.Dlc, i.Local }));
            API.EmitServer(ThermalEvents.SetValues, requestId, items[0].Model, pieces, value);
        }

        [OnNetEvent(ThermalEvents.SetResult)]
        public void OnSetResult(int requestId, bool success, string failure)
        {
            if (_plugin is null || !_pendingResults.Remove(requestId, out string? message)) return;

            if (success)
            {
                _plugin.Notify(NotifyStyle.Success, Text.Literal(message));
                return;
            }

            _plugin.Notify(NotifyStyle.Error, Text.Key(failure switch
            {
                "permission" => "thermal.failed.permission",
                "save" => "thermal.failed.save",
                _ => "thermal.failed.invalid"
            }));

            if (failure != "save")
                API.EmitServer(ThermalEvents.RequestFullSync);
        }

        [OnNetEvent(ThermalEvents.FullSync)]
        public void OnFullSync(string json)
        {
            try
            {
                var map = JsonSerializer.Deserialize<Dictionary<string, float>>(json);
                if (map is null) return;
                _index = map;
                _synced = true;

                API.EmitServer(ThermalEvents.MoveRateChanged, _sentMoveRate);

                foreach (var key in _rows.Keys.ToList())
                    if (!_pendingSends.ContainsKey(key))
                        SetRow(key, Lookup(key, out bool found), found);

                API.Log.Info($"[thermal] full sync received, {_index.Count} entries");
            }
            catch (Exception ex)
            {
                API.Log.Error($"[thermal] full sync failed: {ex.Message}");
            }
        }

        [OnNetEvent(ThermalEvents.ValueUpdated)]
        private void OnValueUpdated(string model, string type, int id, string dlc, int local, float value)
        {
            string key = Key(model, type, id, dlc, local);
            if (_pendingSends.ContainsKey(key)) return;
            SetRow(key, value, true);
        }

        private static void AddThermalTranslations(VMenuPlugin plugin)
        {
            plugin.Translations.Add("en", new Dictionary<string, string>
            {
                ["thermal.description"] = "Edit thermal values for clothing items.",
                ["thermal.worn_list"] = "Worn clothing",
                ["thermal.worn_list.desc"] = "Every component and prop you are wearing. Scroll left or right on a row to change its thermal value, or press X to type one.",
                ["thermal.type_value"] = "Type value",
                ["thermal.type_value.desc"] = "Type a thermal value for the highlighted piece",
                ["thermal.set_all"] = "Set all worn pieces",
                ["thermal.set_all.desc"] = "Apply one thermal value to every component and prop you are wearing.",
                ["thermal.set_all.prompt"] = "Thermal value for all (0 to 20, steps of 0.5)",
                ["thermal.invalid"] = "Enter a number from 0 to 20 in steps of 0.5.",
                ["thermal.failed.permission"] = "You are not allowed to edit thermal values.",
                ["thermal.failed.invalid"] = "The server rejected that thermal value.",
                ["thermal.failed.save"] = "The value was applied, but the server could not save clothes.json.",
                ["thermal.reload"] = "Force full resync",
                ["thermal.reload.desc"] = "Ask the server for the complete thermal map again.",
                ["thermal.reloading"] = "Requesting full thermal map…",
                ["thermal.show_total"] = "Show current total thermal",
                ["thermal.show_total.desc"] = "Display the sum of all thermal values on the clothes you are wearing.",
            });
        }
    }
}