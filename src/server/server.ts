import { ServerPluginDeclaration, VMenuServer } from '@vespura/vmenu-plugin/server';
import {
  clamp,
  ComponentNames,
  isOnStep,
  MaxThermal,
  MinThermal,
  PropNames,
  SetFailures,
  ThermalEvents,
  thermalKey,
  ThermalStep,
  type Piece,
  type SetFailure,
  type ThermalMap,
} from '../shared/thermal';

interface FileItem {
  Global: number;
  Local: number;
  Textures: number;
  ThermalValue: number;
}

interface FileDlc {
  Dlc: string;
  Items: FileItem[];
}

interface FileCategory {
  Type: string;
  Id: number;
  Name: string;
  Dlcs: FileDlc[];
}

interface FileModel {
  Model: string;
  Categories: FileCategory[];
}

interface FileRoot {
  Models: FileModel[];
  Model?: string;
  Categories?: FileCategory[];
}

const FileName = 'clothes.json';
const LegacyModel = 'mp_m_freemode_01';
const MaxPiecesPerRequest = 32;
const MaxModelLength = 64;

const MinMoveRate = 0.1;
const MaxMoveRate = 1.0;

const resourceName = GetCurrentResourceName();

let root: FileRoot = { Models: [] };
let index = new Map<string, number>();
let syncMap: ThermalMap | null = null;
const moveRates = new Map<number, number>();

onNet(ThermalEvents.RequestFullSync, () => {
  const player = source;

  syncMap ??= Object.fromEntries(index);
  emitNet(ThermalEvents.FullSync, player, syncMap);

  moveRates.delete(player);

  for (const [serverId, rate] of [...moveRates]) {
    if (!isConnected(serverId)) {
      moveRates.delete(serverId);
      continue;
    }

    emitNet(ThermalEvents.MoveRateSync, player, serverId, rate);
  }
});

on('playerDropped', () => {
  const player = source;

  if (moveRates.delete(player)) {
    emitNet(ThermalEvents.MoveRateSync, -1, player, MaxMoveRate);
  }
});

onNet(ThermalEvents.MoveRateChanged, (rate: unknown) => {
  const player = source;

  if (typeof rate !== 'number' || !Number.isFinite(rate)) {
    return;
  }

  const clamped = clamp(rate, MinMoveRate, MaxMoveRate);

  if (clamped >= 1) {
    moveRates.delete(player);
  } else {
    moveRates.set(player, clamped);
  }

  emitNet(ThermalEvents.MoveRateSync, -1, player, clamped);
});

onNet(ThermalEvents.SetValues, (requestId: unknown, model: unknown, rawPieces: unknown, value: unknown) => {
  const player = source;
  const name = GetPlayerName(String(player));

  if (!VMenuServer.isPlayerAllowed(player, 'Edit')) {
    console.warn(`[thermal] ${name} tried to edit thermal without permission`);
    reply(player, requestId, false, SetFailures.Permission);
    return;
  }

  const pieces = parsePieces(rawPieces);

  if (!isValidModel(model) || !isValidThermal(value) || pieces === null) {
    console.warn(`[thermal] ${name} sent an invalid thermal edit: ${String(model)} ${JSON.stringify(rawPieces)} = ${String(value)}`);
    reply(player, requestId, false, SetFailures.Invalid);
    return;
  }

  const changed: Piece[] = [];

  for (const piece of pieces) {
    const key = thermalKey(model, piece.Type, piece.Id, piece.Dlc, piece.Local);

    if (index.get(key) === value) {
      continue;
    }

    index.set(key, value);
    syncMap = null;
    getOrAddItem(model, piece).ThermalValue = value;
    changed.push(piece);
  }

  const saved = changed.length === 0 || saveToFile();

  for (const piece of changed) {
    emitNet(ThermalEvents.ValueUpdated, -1, model, piece.Type, piece.Id, piece.Dlc, piece.Local, value);
  }

  reply(player, requestId, saved, saved ? '' : SetFailures.Save);
  console.log(`[thermal] ${name} set ${changed.length} piece(s) on ${model} to ${value.toFixed(1)}`);
});

function isConnected(serverId: number): boolean {
  return Boolean(GetPlayerName(String(serverId)));
}

function reply(player: number, requestId: unknown, success: boolean, failure: SetFailure | ''): void {
  emitNet(ThermalEvents.SetResult, player, requestId, success, failure);
}

function parsePieces(raw: unknown): Piece[] | null {
  if (!Array.isArray(raw) || raw.length === 0 || raw.length > MaxPiecesPerRequest) {
    return null;
  }

  const pieces: Piece[] = [];

  for (const entry of raw as unknown[]) {
    if (typeof entry !== 'object' || entry === null) {
      return null;
    }

    const { Type, Id, Dlc, Local } = entry as Record<string, unknown>;

    if ((Type !== 'component' && Type !== 'prop') || !Number.isInteger(Id) || !Number.isInteger(Local)) {
      return null;
    }

    if (Dlc !== undefined && Dlc !== null && typeof Dlc !== 'string') {
      return null;
    }

    pieces.push({ Type, Id: Id as number, Dlc: Dlc ?? '', Local: Local as number });
  }

  return pieces;
}

function isValidModel(model: unknown): model is string {
  return typeof model === 'string' && model.length <= MaxModelLength && /^[A-Za-z0-9_]+$/.test(model);
}

function isValidThermal(value: unknown): value is number {
  return typeof value === 'number' && value >= MinThermal && value <= MaxThermal && isOnStep(value, ThermalStep);
}

function getOrAddItem(model: string, piece: Piece): FileItem {
  let fileModel = root.Models.find((m) => m.Model === model);

  if (!fileModel) {
    fileModel = { Model: model, Categories: [] };
    root.Models.push(fileModel);
  }

  let category = fileModel.Categories.find((c) => c.Type === piece.Type && c.Id === piece.Id);

  if (!category) {
    category = {
      Type: piece.Type,
      Id: piece.Id,
      Name:
        piece.Type === 'component'
          ? (ComponentNames.get(piece.Id) ?? `Component ${piece.Id}`)
          : (PropNames.get(piece.Id) ?? `Prop ${piece.Id}`),
      Dlcs: [],
    };
    fileModel.Categories.push(category);
  }

  let fileDlc = category.Dlcs.find((d) => d.Dlc === piece.Dlc);

  if (!fileDlc) {
    fileDlc = { Dlc: piece.Dlc, Items: [] };
    category.Dlcs.push(fileDlc);
  }

  let item = fileDlc.Items.find((i) => i.Local === piece.Local);

  if (!item) {
    item = { Global: 0, Local: piece.Local, Textures: 0, ThermalValue: 0 };
    fileDlc.Items.push(item);
  }

  return item;
}

function loadFromFile(): void {
  const json = LoadResourceFile(resourceName, FileName);

  if (!json || json.trim().length === 0) {
    console.warn(`[thermal] could not read ${FileName}`);
    return;
  }

  try {
    const loaded = JSON.parse(json) as FileRoot | null;

    if (!loaded) {
      return;
    }

    loaded.Models ??= [];

    if (loaded.Categories && loaded.Categories.length > 0) {
      loaded.Models.push({ Model: loaded.Model ?? LegacyModel, Categories: loaded.Categories });
    }

    delete loaded.Model;
    delete loaded.Categories;

    const loadedIndex = new Map<string, number>();

    for (const model of loaded.Models) {
      model.Model ??= '';
      model.Categories ??= [];

      for (const category of model.Categories) {
        category.Type ??= '';
        category.Id ??= 0;
        category.Name ??= '';
        category.Dlcs ??= [];

        for (const dlc of category.Dlcs) {
          dlc.Dlc ??= '';
          dlc.Items ??= [];

          for (const item of dlc.Items) {
            item.Global ??= 0;
            item.Local ??= 0;
            item.Textures ??= 0;
            item.ThermalValue ??= 0;

            loadedIndex.set(thermalKey(model.Model, category.Type, category.Id, dlc.Dlc, item.Local), item.ThermalValue);
          }
        }
      }
    }

    root = loaded;
    index = loadedIndex;
    syncMap = null;

    console.log(`[thermal] loaded ${index.size} entries for ${root.Models.length} model(s) from ${FileName}`);
  } catch (error) {
    console.error(`[thermal] failed to parse ${FileName}: ${error instanceof Error ? error.message : String(error)}`);
  }
}

function saveToFile(): boolean {
  if (SaveResourceFile(resourceName, FileName, JSON.stringify(root, null, 2), -1)) {
    return true;
  }

  console.error(`[thermal] SaveResourceFile failed for ${FileName}`);
  return false;
}

loadFromFile();

const declaration = new ServerPluginDeclaration('Thermal Clothing')
  .addPermission('Edit', 'Lets a player edit / add thermal values for clothing.')
  .addBoolSetting('Enabled', true, 'Turns the thermal clothing editor on or off.');

VMenuServer.register(declaration).then((result) => console.log(`[thermal] Server registered with vMenu: ${result.accepted}`));
