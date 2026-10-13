export const ThermalEvents = {
  RequestFullSync: 'vMenu.Clothing:Thermal:RequestFullSync',
  FullSync: 'vMenu.Clothing:Thermal:FullSync',
  SetValues: 'vMenu.Clothing:Thermal:SetValues',
  SetResult: 'vMenu.Clothing:Thermal:SetResult',
  ValueUpdated: 'vMenu.Clothing:Thermal:ValueUpdated',
  MoveRateChanged: 'vMenu.Clothing:Thermal:MoveRateChanged',
  MoveRateSync: 'vMenu.Clothing:Thermal:MoveRateSync',
} as const;

export const SetFailures = {
  Permission: 'permission',
  Invalid: 'invalid',
  Save: 'save',
} as const;

export type SetFailure = (typeof SetFailures)[keyof typeof SetFailures];

export const MinThermal = 0;
export const MaxThermal = 100;
export const ThermalStep = 0.5;

export const ThermalRangeText = `${MinThermal} to ${MaxThermal}, steps of ${ThermalStep}`;

export type PieceType = 'component' | 'prop';

export interface Piece {
  Type: PieceType;
  Id: number;
  Dlc: string;
  Local: number;
}

export type ThermalMap = Record<string, number>;

export const ComponentNames: ReadonlyMap<number, string> = new Map([
  [0, 'Head'],
  [1, 'Mask'],
  [2, 'Hair'],
  [3, 'Torso/Arms'],
  [4, 'Legs'],
  [5, 'Bags'],
  [6, 'Shoes'],
  [7, 'Accessories'],
  [8, 'Undershirt'],
  [9, 'Body Armor'],
  [10, 'Decals'],
  [11, 'Jackets/Tops'],
]);

export const PropNames: ReadonlyMap<number, string> = new Map([
  [0, 'Hats'],
  [1, 'Glasses'],
  [2, 'Ears'],
  [6, 'Watches'],
  [7, 'Bracelets'],
]);

export function thermalKey(model: string, type: string, id: number, dlc: string, local: number): string {
  return `${model}|${type}|${id}|${dlc ? dlc : 'base game'}|${local}`;
}

export function isOnStep(value: number, step: number): boolean {
  return Math.abs(value / step - Math.round(value / step)) < 0.0001;
}

export function clamp(value: number, min: number, max: number): number {
  return Math.min(Math.max(value, min), max);
}
