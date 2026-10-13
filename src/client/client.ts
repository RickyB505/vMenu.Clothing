import { Gate, NotifyStyle, Text, VMenuPlugin, type PluginItem, type PluginList } from '@vespura/vmenu-plugin/client';
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
  ThermalRangeText,
  ThermalStep,
  type Piece,
  type PieceType,
  type ThermalMap,
} from '../shared/thermal';

interface WornThermal {
  readonly model: string;
  readonly type: PieceType;
  readonly id: number;
  readonly name: string;
  readonly dlc: string;
  readonly local: number;
  readonly thermalValue: number;
  readonly found: boolean;
}

type BodyState = 'WayTooCold' | 'TooCold' | 'Normal' | 'TooHot' | 'WayTooHot';

const Points: readonly { temperature: number; thermalValue: number }[] = [
  { temperature: -273.15, thermalValue: 5000.0 },
  { temperature: -50.0, thermalValue: 400.0 },
  { temperature: -25.0, thermalValue: 330.0 },
  { temperature: -10.0, thermalValue: 200.0 },
  { temperature: 0.0, thermalValue: 140.0 },
  { temperature: 18.0, thermalValue: 85.0 },
  { temperature: 30.0, thermalValue: 50.0 },
  { temperature: 45.0, thermalValue: 15.0 },
  { temperature: 60.0, thermalValue: 0.0 },
];

const NormalBodyTemp = 37.0;
const MaxTempSwing = 15.0;
const ApproachRate = 0.05;

const DefaultThermal = 2.5;

const TickIntervalMs = 1000;
const SendDelayMs = 500;

const DamageMin = 1;
const DamageMax = 2;

const UseTimecycle = true;
const ColdTimecycle = 'hud_def_desat_cold';
const HotTimecycle = 'REDMIST_blend';
const TooTimecycleStrength = 0.4;
const WayTooTimecycleStrength = 1.0;

const TooHotStaminaDrainPerSec = 0.05;
const WayTooHotStaminaDrainPerSec = 0.15;

const TooColdMoveRate = 0.85;
const WayTooColdMoveRate = 0.65;

const DebugLogging = false;

const disablePlayerHealthRecharge = (player: number): void => Citizen.invokeNative('0xBCB06442F7E52666', player);

const hash = (name: string): number => GetHashKey(name) >>> 0;

const MaleModel = hash('mp_m_freemode_01');
const FemaleModel = hash('mp_f_freemode_01');

const ModelNames = new Map<number, string>(
  ['mp_m_freemode_01', 'mp_f_freemode_01', 'player_one', 'player_two', 'player_zero'].map((name) => [hash(name), name]),
);

let bodyTemp = NormalBodyTemp;
let index = new Map<string, number>();
let synced = false;

let appliedTimecycle: string | null = null;
let appliedTimecycleStrength = -1;
let sentMoveRate = 1;
const remoteMoveRates = new Map<number, number>();

const rows = new Map<string, { item: WornThermal; row: PluginList }>();
const pendingSends = new Map<string, ReturnType<typeof setTimeout>>();
const pendingResults = new Map<number, string>();
let nextRequestId = 0;

const ThermalOptions = Array.from({ length: Math.trunc((MaxThermal - MinThermal) / ThermalStep) + 1 }, (_, i) =>
  Text.literal(formatThermal(MinThermal + i * ThermalStep)),
);

function currentBodyState(): BodyState {
  if (bodyTemp < 24.0) return 'WayTooCold';
  if (bodyTemp < 30.0) return 'TooCold';
  if (bodyTemp > 50.0) return 'WayTooHot';
  if (bodyTemp > 46.0) return 'TooHot';
  return 'Normal';
}

function currentTempAtHeight(height: number): number {
  return IsInteriorScene() ? 21.0 : (exports['vMenu.Enhanced']!.GetTemperatureAtHeight!(height) as number);
}

function getRequiredThermal(temperature: number): number {
  if (temperature <= Points[0]!.temperature) {
    return Points[0]!.thermalValue;
  }

  for (let i = 0; i < Points.length - 1; i++) {
    const a = Points[i]!;
    const b = Points[i + 1]!;

    if (temperature <= b.temperature) {
      const t = (temperature - a.temperature) / (b.temperature - a.temperature);
      return a.thermalValue + t * (b.thermalValue - a.thermalValue);
    }
  }

  return Points[Points.length - 1]!.thermalValue;
}

function modelName(ped: number): string {
  const model = GetEntityModel(ped) >>> 0;
  return ModelNames.get(model) ?? `0x${model.toString(16).toUpperCase().padStart(8, '0')}`;
}

function isFreemodePed(ped: number): boolean {
  const model = GetEntityModel(ped) >>> 0;
  return model === MaleModel || model === FemaleModel;
}

function itemKey(item: WornThermal): string {
  return thermalKey(item.model, item.type, item.id, item.dlc, item.local);
}

function lookup(key: string): { value: number; found: boolean } {
  const value = index.get(key);
  return value === undefined ? { value: DefaultThermal, found: false } : { value, found: true };
}

function getWornThermal(ped: number): { items: WornThermal[]; total: number } {
  const items: WornThermal[] = [];
  const model = modelName(ped);
  let total = 0;

  const add = (type: PieceType, id: number, name: string, dlc: string, local: number): void => {
    const { value, found } = lookup(thermalKey(model, type, id, dlc, local));
    total += value;
    items.push({ model, type, id, name, dlc, local, thermalValue: value, found });
  };

  for (const [id, name] of ComponentNames) {
    const drawable = GetPedDrawableVariation(ped, id);
    add('component', id, name, GetPedCollectionNameFromDrawable(ped, id, drawable) ?? '', GetPedCollectionLocalIndexFromDrawable(ped, id, drawable));
  }

  for (const [id, name] of PropNames) {
    const prop = GetPedPropIndex(ped, id);

    if (prop === -1) {
      continue;
    }

    add('prop', id, name, GetPedCollectionNameFromProp(ped, id, prop) ?? '', GetPedCollectionLocalIndexFromProp(ped, id, prop));
  }

  return { items, total };
}

function getCurrentThermalValue(): number {
  return getWornThermal(PlayerPedId()).total;
}

let lastTime = GetGameTimer();

setInterval(() => {
  const now = GetGameTimer();
  const dt = (now - lastTime) / 1000;
  lastTime = now;

  if (!synced) {
    return;
  }

  const ped = PlayerPedId();

  if (!isFreemodePed(ped)) {
    return;
  }

  if (IsPedDeadOrDying(ped, true)) {
    bodyTemp = NormalBodyTemp;
    return;
  }

  const ambient = currentTempAtHeight(GetEntityCoords(ped, false)[2]!);
  const clothing = getCurrentThermalValue();
  const deficit = getRequiredThermal(ambient) - clothing;
  const factor = clamp(deficit / 200, -1, 1);
  const target = NormalBodyTemp - factor * MaxTempSwing;
  bodyTemp = target + (bodyTemp - target) * Math.exp(-ApproachRate * dt);

  const state = currentBodyState();

  if (DebugLogging) {
    console.log(`[thermal] body ${bodyTemp.toFixed(2)} target ${target.toFixed(2)} ${state} clothing ${clothing.toFixed(1)}`);
  }

  if (state === 'WayTooCold' || state === 'WayTooHot') {
    const damage = DamageMin + Math.floor(Math.random() * (DamageMax - DamageMin + 1));
    SetEntityHealth(ped, GetEntityHealth(ped) - damage);
  }
}, TickIntervalMs);

function getMoveRate(state: BodyState): number {
  switch (state) {
    case 'WayTooCold':
      return WayTooColdMoveRate;
    case 'TooCold':
      return TooColdMoveRate;
    default:
      return 1;
  }
}

function getTimecycle(state: BodyState): string | null {
  switch (state) {
    case 'TooCold':
    case 'WayTooCold':
      return ColdTimecycle;
    case 'TooHot':
    case 'WayTooHot':
      return HotTimecycle;
    default:
      return null;
  }
}

function getTimecycleStrength(state: BodyState): number {
  switch (state) {
    case 'WayTooCold':
    case 'WayTooHot':
      return WayTooTimecycleStrength;
    case 'TooCold':
    case 'TooHot':
      return TooTimecycleStrength;
    default:
      return 0;
  }
}

function getStaminaDrainPerSec(state: BodyState): number {
  switch (state) {
    case 'WayTooHot':
      return WayTooHotStaminaDrainPerSec;
    case 'TooHot':
      return TooHotStaminaDrainPerSec;
    default:
      return 0;
  }
}

setTick(() => {
  const ped = PlayerPedId();
  const player = PlayerId();
  const active = synced && isFreemodePed(ped) && !IsPedDeadOrDying(ped, true);
  const state = active ? currentBodyState() : 'Normal';

  if (state === 'WayTooCold' || state === 'WayTooHot') {
    disablePlayerHealthRecharge(player);
  }

  const drain = getStaminaDrainPerSec(state);

  if (drain > 0 && IsPedSprinting(ped)) {
    RestorePlayerStamina(player, -drain * GetFrameTime());
  }

  const moveRate = getMoveRate(state);

  if (moveRate !== 1) {
    SetPedMoveRateOverride(ped, moveRate);
  }

  if (moveRate !== sentMoveRate) {
    if (moveRate === 1) {
      SetPedMoveRateOverride(ped, 1);
    }

    sentMoveRate = moveRate;
    emitNet(ThermalEvents.MoveRateChanged, moveRate);
  }

  const timecycle = UseTimecycle ? getTimecycle(state) : null;

  if (timecycle !== appliedTimecycle) {
    if (timecycle === null) {
      ClearTimecycleModifier();
    } else {
      SetTimecycleModifier(timecycle);
    }

    appliedTimecycle = timecycle;
    appliedTimecycleStrength = -1;
  }

  if (timecycle !== null) {
    const strength = getTimecycleStrength(state);

    if (strength !== appliedTimecycleStrength) {
      SetTimecycleModifierStrength(strength);
      appliedTimecycleStrength = strength;
    }
  }

  for (const [serverId, rate] of remoteMoveRates) {
    const remote = GetPlayerFromServerId(serverId);

    if (remote === -1 || remote === player) {
      continue;
    }

    const remotePed = GetPlayerPed(remote);

    if (DoesEntityExist(remotePed)) {
      SetPedMoveRateOverride(remotePed, rate);
    }
  }
});

onNet(ThermalEvents.MoveRateSync, (serverId: number, rate: number) => {
  if (rate !== 1) {
    remoteMoveRates.set(serverId, rate);
    return;
  }

  remoteMoveRates.delete(serverId);

  const remote = GetPlayerFromServerId(serverId);

  if (remote !== -1 && remote !== PlayerId()) {
    const remotePed = GetPlayerPed(remote);

    if (DoesEntityExist(remotePed)) {
      SetPedMoveRateOverride(remotePed, 1);
    }
  }
});

function formatThermal(value: number): string {
  return value.toFixed(1);
}

function toIndex(value: number): number {
  return Math.round((clamp(value, MinThermal, MaxThermal) - MinThermal) / ThermalStep);
}

function fromIndex(index: number): number {
  return MinThermal + index * ThermalStep;
}

function parseThermal(input: string | null): number | null {
  const text = input?.trim().replace(/,/g, '.') ?? '';

  if (!/^[+-]?(\d+\.?\d*|\.\d+)(e[+-]?\d+)?$/i.test(text)) {
    return null;
  }

  const value = Number(text);

  if (value < MinThermal || value > MaxThermal) {
    return null;
  }

  return isOnStep(value, ThermalStep) ? value : null;
}

const plugin = VMenuPlugin.create('Thermal Clothing');

plugin.descriptionKey = 'thermal.description';
plugin.rootMenu.subtitle = Text.literal('Thermal Clothing Editor');

plugin.translations.add('en', {
  'thermal.description': 'Edit thermal values for clothing items.',
  'thermal.worn_list': 'Worn clothing',
  'thermal.worn_list.desc':
    'Every component and prop you are wearing. Scroll left or right on a row to change its thermal value, or press X to type one.',
  'thermal.type_value': 'Type value',
  'thermal.type_value.desc': 'Type a thermal value for the highlighted piece',
  'thermal.set_all': 'Set all worn pieces',
  'thermal.set_all.desc': 'Apply one thermal value to every component and prop you are wearing.',
  'thermal.set_all.prompt': `Thermal value for all (${ThermalRangeText})`,
  'thermal.invalid': `Enter a number from ${MinThermal} to ${MaxThermal} in steps of ${ThermalStep}.`,
  'thermal.failed.permission': 'You are not allowed to edit thermal values.',
  'thermal.failed.invalid': 'The server rejected that thermal value.',
  'thermal.failed.save': 'The value was applied, but the server could not save clothes.json.',
  'thermal.reload': 'Force full resync',
  'thermal.reload.desc': 'Ask the server for the complete thermal map again.',
  'thermal.reloading': 'Requesting full thermal map…',
  'thermal.show_total': 'Show current total thermal',
  'thermal.show_total.desc': 'Display the sum of all thermal values on the clothes you are wearing.',
});

exports('GetBodyState', () => currentBodyState());
exports('GetTotalThermalValue', () => getCurrentThermalValue());
exports('GetBodyTemperature', () => bodyTemp);

emitNet(ThermalEvents.RequestFullSync);

const enabled = plugin.settings.bool('Enabled', true, 'Turns the thermal clothing editor on or off.');
const gate = Gate.permission('Edit').and(Gate.setting(enabled));

const wornSub = plugin.rootMenu.addSubmenu(Text.key('thermal.worn_list'), {
  subtitle: Text.literal('Thermal Editor'),
  description: Text.key('thermal.worn_list.desc'),
  gate,
});

wornSub.menu.onOpened(rebuildWornMenu);

const typeValue = wornSub.menu.addKey('type_value', Text.key('thermal.type_value'), 'X', {
  defaultButton: 'RLEFT_INDEX',
  description: Text.key('thermal.type_value.desc'),
  shadowedControl: 73,
});

typeValue.onPressed((press) => void typeValueAsync(press.item));

const reload = plugin.rootMenu.addButton(Text.key('thermal.reload'), {
  description: Text.key('thermal.reload.desc'),
  gate,
});

reload.onSelected(() => {
  emitNet(ThermalEvents.RequestFullSync);
  plugin.notify(NotifyStyle.Info, Text.key('thermal.reloading'));
});

const totalButton = plugin.rootMenu.addButton(Text.key('thermal.show_total'), {
  description: Text.key('thermal.show_total.desc'),
});

totalButton.onSelected(() =>
  plugin.notify(NotifyStyle.Info, Text.literal(`Current total thermal: ${formatThermal(getCurrentThermalValue())}`)),
);

plugin.connect().then((result) => console.log(`[thermal] Registered with vMenu: ${result.accepted}`));

function rebuildWornMenu(): void {
  const { items, total } = getWornThermal(PlayerPedId());
  const menu = wornSub.menu;
  rows.clear();

  plugin.batch(() => {
    menu.clear();
    menu.subtitle = Text.literal(`Total thermal: ${formatThermal(total)}`);

    for (const item of items) {
      const label = `[${item.type === 'prop' ? 'P' : 'C'}${item.id}] ${item.name}`;
      const row = menu.addList(Text.literal(label), ThermalOptions, {
        selectedIndex: toIndex(item.thermalValue),
        description: buildRowDescription(item),
      });

      const key = itemKey(item);
      rows.set(key, { item, row });
      row.onIndexChanged((_, newIndex) => queueSend(key, fromIndex(newIndex)));
    }

    const setAll = menu.addButton(Text.key('thermal.set_all'), {
      description: Text.key('thermal.set_all.desc'),
    });

    setAll.onSelected(() => void setAllAsync());
  });
}

async function setAllAsync(): Promise<void> {
  if (rows.size === 0) {
    return;
  }

  const input = await plugin.getText(Text.key('thermal.set_all.prompt'), { maxLength: 5 });

  if (!input?.trim()) {
    return;
  }

  const value = parseThermal(input);

  if (value === null) {
    plugin.notify(NotifyStyle.Error, Text.key('thermal.invalid'));
    return;
  }

  const items = [...rows.values()].map((r) => r.item);

  updateRows(() => {
    for (const item of items) {
      const key = itemKey(item);
      cancelPendingSend(key);
      setRow(key, value, true);
    }
  });

  send(items, value, `All ${items.length} worn pieces set to ${formatThermal(value)}.`);
}

async function typeValueAsync(row: PluginItem | undefined): Promise<void> {
  const match = [...rows].find(([, entry]) => entry.row === row);

  if (!match) {
    return;
  }

  const [key, { item }] = match;

  const input = await plugin.getText(Text.literal(`Thermal value for ${item.name} (${ThermalRangeText})`), {
    maxLength: 5,
    initialValue: formatThermal(item.thermalValue),
  });

  if (!input?.trim()) {
    return;
  }

  const value = parseThermal(input);

  if (value === null) {
    plugin.notify(NotifyStyle.Error, Text.key('thermal.invalid'));
    return;
  }

  queueSend(key, value);
}

function buildRowDescription(item: WornThermal): Text {
  const dlc = item.dlc ? item.dlc : 'base game';
  const status = item.found ? '' : `~n~Using the default of ${formatThermal(DefaultThermal)}, not saved yet.`;

  return Text.literal(
    `${item.name} (${item.type} ${item.id})~n~DLC: ${dlc}, local index: ${item.local}${status}~n~Press X to type a value.`,
  );
}

function setRow(key: string, value: number, found: boolean): void {
  if (found) {
    index.set(key, value);
  }

  const entry = rows.get(key);

  if (!entry) {
    return;
  }

  const updated: WornThermal = { ...entry.item, thermalValue: value, found };
  rows.set(key, { item: updated, row: entry.row });
  entry.row.description = buildRowDescription(updated);

  const selected = toIndex(value);

  if (entry.row.selectedIndex !== selected) {
    entry.row.selectedIndex = selected;
  }
}

function updateRows(changes: () => void): void {
  plugin.batch(() => {
    changes();

    const total = [...rows.values()].reduce((sum, r) => sum + r.item.thermalValue, 0);
    wornSub.menu.subtitle = Text.literal(`Total thermal: ${formatThermal(total)}`);
  });
}

function cancelPendingSend(key: string): void {
  const pending = pendingSends.get(key);

  if (pending !== undefined) {
    clearTimeout(pending);
    pendingSends.delete(key);
  }
}

function queueSend(key: string, value: number): void {
  const entry = rows.get(key);

  if (!entry || (entry.item.found && entry.item.thermalValue === value)) {
    return;
  }

  cancelPendingSend(key);

  const item = entry.item;

  pendingSends.set(
    key,
    setTimeout(() => {
      pendingSends.delete(key);
      send([item], value, `${item.name} set to ${formatThermal(value)}.`);
    }, SendDelayMs),
  );

  updateRows(() => setRow(key, value, true));
}

function send(items: readonly WornThermal[], value: number, successMessage: string): void {
  const requestId = ++nextRequestId;
  pendingResults.set(requestId, successMessage);

  const pieces: Piece[] = items.map((i) => ({ Type: i.type, Id: i.id, Dlc: i.dlc, Local: i.local }));
  emitNet(ThermalEvents.SetValues, requestId, items[0]!.model, pieces, value);
}

onNet(ThermalEvents.SetResult, (requestId: number, success: boolean, failure: string) => {
  const message = pendingResults.get(requestId);

  if (message === undefined) {
    return;
  }

  pendingResults.delete(requestId);

  if (success) {
    plugin.notify(NotifyStyle.Success, Text.literal(message));
    return;
  }

  const failureKey =
    failure === SetFailures.Permission
      ? 'thermal.failed.permission'
      : failure === SetFailures.Save
        ? 'thermal.failed.save'
        : 'thermal.failed.invalid';

  plugin.notify(NotifyStyle.Error, Text.key(failureKey));

  if (failure !== SetFailures.Save) {
    emitNet(ThermalEvents.RequestFullSync);
  }
});

onNet(ThermalEvents.FullSync, (map: unknown) => {
  if (typeof map !== 'object' || map === null || Array.isArray(map)) {
    console.error('[thermal] full sync failed: the server sent no thermal map');
    return;
  }

  index = new Map(
    Object.entries(map as ThermalMap).filter((entry): entry is [string, number] => typeof entry[1] === 'number'),
  );
  synced = true;

  emitNet(ThermalEvents.MoveRateChanged, sentMoveRate);

  updateRows(() => {
    for (const key of rows.keys()) {
      if (!pendingSends.has(key)) {
        const { value, found } = lookup(key);
        setRow(key, value, found);
      }
    }
  });

  console.log(`[thermal] full sync received, ${index.size} entries`);
});

onNet(
  ThermalEvents.ValueUpdated,
  (model: string, type: string, id: number, dlc: string, local: number, value: number) => {
    const key = thermalKey(model, type, id, dlc, local);

    if (!pendingSends.has(key)) {
      updateRows(() => setRow(key, value, true));
    }
  },
);
