using CitizenFX.FiveM.Server;
using CitizenFX.FiveM.Server.Entities;
using CitizenFX.FiveM.Shared.Script;
using CitizenFX.FiveM.Shared.Serialization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using vMenu.Enhanced.ServerAPI;

namespace vMenu.Clothing.Server
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

    public static class SetFailures
    {
        public const string Permission = "permission";
        public const string Invalid = "invalid";
        public const string Save = "save";
    }

    public sealed class Main : IScript
    {
        private const string FileName = "clothes.json";
        private const string LegacyModel = "mp_m_freemode_01";
        private const float MinThermal = 0.0f;
        private const float MaxThermal = 200.0f;
        private const float ThermalStep = 0.5f;
        private const int MaxPiecesPerRequest = 32;
        private const int MaxModelLength = 64;

        private const float MinMoveRate = 0.1f;
        private const float MaxMoveRate = 1.0f;

        private static FileRoot _root = new();
        private static readonly Dictionary<string, float> Index = [];

        private static readonly Dictionary<int, float> MoveRates = [];

        private static readonly JsonSerializerOptions ReadOpts = new() { PropertyNameCaseInsensitive = true };

        private static readonly JsonSerializerOptions WriteOpts = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public async void Initialize()
        {
            LoadFromFile();

            var declaration = new ServerPluginDeclaration("Thermal Clothing")
                .AddPermission("Edit", "Lets a player edit / add thermal values for clothing.")
                .AddBoolSetting("Enabled", true, "Turns the thermal clothing editor on or off.");

            var result = await VMenuServer.RegisterAsync(declaration);
            API.Log.Info($"[thermal] Server registered with vMenu: {result.Accepted}");
        }

        [OnNetEvent(ThermalEvents.RequestFullSync)]
        public void OnRequestFullSync([FromSource] Player player)
        {
            API.EmitClient(player.Handle, ThermalEvents.FullSync, JsonSerializer.Serialize(Index));

            MoveRates.Remove(player.Handle);

            foreach (var serverId in MoveRates.Keys.ToList())
            {
                if (!IsConnected(serverId))
                {
                    MoveRates.Remove(serverId);
                    continue;
                }
                API.EmitClient(player.Handle, ThermalEvents.MoveRateSync, serverId, MoveRates[serverId]);
            }
        }

        [OnNetEvent(ThermalEvents.MoveRateChanged)]
        public void OnMoveRateChanged([FromSource] Player player, float rate)
        {
            if (float.IsNaN(rate) || float.IsInfinity(rate)) return;

            rate = Math.Clamp(rate, MinMoveRate, MaxMoveRate);

            if (rate >= 1f) MoveRates.Remove(player.Handle);
            else MoveRates[player.Handle] = rate;

            API.EmitClient(-1, ThermalEvents.MoveRateSync, player.Handle, rate);
        }

        [OnNetEvent(ThermalEvents.SetValues)]
        public void OnSetValues([FromSource] Player player, int requestId, string model, string piecesJson, float value)
        {
            if (!VMenuServer.IsPlayerAllowed(player.StrHandle, "Edit"))
            {
                API.Log.Warn($"[thermal] {player.Name} tried to edit thermal without permission");
                Reply(player, requestId, false, SetFailures.Permission);
                return;
            }

            var pieces = ParsePieces(piecesJson);
            if (!IsValidModel(model) || !IsValidThermal(value) || pieces is null)
            {
                API.Log.Warn($"[thermal] {player.Name} sent an invalid thermal edit: {model} {piecesJson} = {value}");
                Reply(player, requestId, false, SetFailures.Invalid);
                return;
            }

            var changed = new List<Piece>();
            foreach (var piece in pieces)
            {
                string key = Key(model, piece.Type, piece.Id, piece.Dlc, piece.Local);
                if (Index.TryGetValue(key, out float current) && current == value) continue;

                Index[key] = value;
                GetOrAddItem(model, piece).ThermalValue = value;
                changed.Add(piece);
            }

            bool saved = changed.Count == 0 || SaveToFile();

            foreach (var piece in changed)
                API.EmitClient(-1, ThermalEvents.ValueUpdated, model, piece.Type, piece.Id, piece.Dlc, piece.Local, value);

            Reply(player, requestId, saved, saved ? "" : SetFailures.Save);
            API.Log.Info($"[thermal] {player.Name} set {changed.Count} piece(s) on {model} to {value:F1}");
        }

        private static bool IsConnected(int serverId)
            => !string.IsNullOrEmpty(Native.GetPlayerName(serverId.ToString()));

        private static void Reply(Player player, int requestId, bool success, string failure)
            => API.EmitClient(player.Handle, ThermalEvents.SetResult, requestId, success, failure);

        private static List<Piece>? ParsePieces(string json)
        {
            List<Piece>? pieces;
            try
            {
                pieces = JsonSerializer.Deserialize<List<Piece>>(json, ReadOpts);
            }
            catch (JsonException)
            {
                return null;
            }

            if (pieces is not { Count: > 0 and <= MaxPiecesPerRequest }) return null;

            foreach (var piece in pieces)
            {
                if (piece.Type is not ("component" or "prop")) return null;
                piece.Dlc ??= "";
            }

            return pieces;
        }

        private static bool IsValidModel(string? model)
            => model is { Length: > 0 and <= MaxModelLength } && model.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

        private static bool IsValidThermal(float value)
            => value >= MinThermal && value <= MaxThermal
               && MathF.Abs(value / ThermalStep - MathF.Round(value / ThermalStep)) < 0.0001f;

        private static string Key(string model, string type, int id, string dlc, int local)
            => $"{model}|{type}|{id}|{(string.IsNullOrEmpty(dlc) ? "base game" : dlc)}|{local}";

        private static FileItem GetOrAddItem(string model, Piece piece)
        {
            var fileModel = _root.Models.FirstOrDefault(m => m.Model == model);
            if (fileModel is null)
            {
                fileModel = new FileModel { Model = model };
                _root.Models.Add(fileModel);
            }

            var category = fileModel.Categories.FirstOrDefault(c => c.Type == piece.Type && c.Id == piece.Id);
            if (category is null)
            {
                category = new FileCategory
                {
                    Type = piece.Type,
                    Id = piece.Id,
                    Name = piece.Type == "component"
                        ? ComponentNames.GetValueOrDefault(piece.Id, $"Component {piece.Id}")
                        : PropNames.GetValueOrDefault(piece.Id, $"Prop {piece.Id}")
                };
                fileModel.Categories.Add(category);
            }

            var fileDlc = category.Dlcs.FirstOrDefault(d => d.Dlc == piece.Dlc);
            if (fileDlc is null)
            {
                fileDlc = new FileDlc { Dlc = piece.Dlc };
                category.Dlcs.Add(fileDlc);
            }

            var item = fileDlc.Items.FirstOrDefault(i => i.Local == piece.Local);
            if (item is null)
            {
                item = new FileItem { Local = piece.Local };
                fileDlc.Items.Add(item);
            }

            return item;
        }

        private static void LoadFromFile()
        {
            string? json = Native.LoadResourceFile(Native.GetCurrentResourceName(), FileName);
            if (string.IsNullOrWhiteSpace(json))
            {
                API.Log.Warn($"[thermal] could not read {FileName}");
                return;
            }

            try
            {
                var root = JsonSerializer.Deserialize<FileRoot>(json, ReadOpts);
                if (root is null) return;

                if (root.Categories is { Count: > 0 } legacy)
                {
                    root.Models.Add(new FileModel { Model = root.Model ?? LegacyModel, Categories = legacy });
                    root.Model = null;
                    root.Categories = null;
                }

                _root = root;
                Index.Clear();
                foreach (var model in root.Models)
                    foreach (var cat in model.Categories)
                        foreach (var dlc in cat.Dlcs)
                            foreach (var item in dlc.Items)
                                Index[Key(model.Model, cat.Type, cat.Id, dlc.Dlc, item.Local)] = item.ThermalValue;

                API.Log.Info($"[thermal] loaded {Index.Count} entries for {root.Models.Count} model(s) from {FileName}");
            }
            catch (Exception ex)
            {
                API.Log.Error($"[thermal] failed to parse {FileName}: {ex.Message}");
            }
        }

        private static bool SaveToFile()
        {
            byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(_root, WriteOpts));

            if (Native.SaveResourceFile(Native.GetCurrentResourceName(), FileName, bytes))
                return true;

            API.Log.Error($"[thermal] SaveResourceFile failed for {FileName}");
            return false;
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
    }

    internal sealed class Piece
    {
        public string Type { get; set; } = "";
        public int Id { get; set; }
        public string Dlc { get; set; } = "";
        public int Local { get; set; }
    }

    internal sealed class FileItem
    {
        public int Global { get; set; }
        public int Local { get; set; }
        public int Textures { get; set; }
        public float ThermalValue { get; set; }
    }

    internal sealed class FileDlc
    {
        public string Dlc { get; set; } = "";
        public List<FileItem> Items { get; set; } = [];
    }

    internal sealed class FileCategory
    {
        public string Type { get; set; } = "";
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public List<FileDlc> Dlcs { get; set; } = [];
    }

    internal sealed class FileModel
    {
        public string Model { get; set; } = "";
        public List<FileCategory> Categories { get; set; } = [];
    }

    internal sealed class FileRoot
    {
        public List<FileModel> Models { get; set; } = [];

        public string? Model { get; set; }

        public List<FileCategory>? Categories { get; set; }
    }
}