import { CapabilityViewModel, ViewJson, ViewTableModel } from '../../../domain';

type ViewObject = { [key: string]: ViewJson };

/**
 * How one view value is drawn, picked from its shape alone (views carry no type hints):
 * - `empty`: missing / null — a dim dash.
 * - `boolean`, `number`, `text`, `link`: one scalar (`link` for an http(s) URL).
 * - `none`: an empty list or map.
 * - `chips`: a list of scalars — one chip each.
 * - `records`: a list of objects, or a map whose values are all maps (a keyed collection) —
 *   one small card each, see {@link toRecords}.
 * - `fields`: any other map — a key/value grid.
 * - `list`: a mixed list — each item drawn on its own.
 */
export type ValueShape =
  | 'empty'
  | 'boolean'
  | 'number'
  | 'text'
  | 'link'
  | 'none'
  | 'chips'
  | 'records'
  | 'fields'
  | 'list';

export function isObject(value: ViewJson): value is ViewObject {
  return value !== null && typeof value === 'object' && !Array.isArray(value);
}

export function isScalar(value: ViewJson): value is string | number | boolean | null {
  return value === null || typeof value !== 'object';
}

export function shapeOf(value: ViewJson): ValueShape {
  if (value === null) return 'empty';
  if (typeof value === 'boolean') return 'boolean';
  if (typeof value === 'number') return 'number';
  if (typeof value === 'string') return /^https?:\/\/\S+$/i.test(value) ? 'link' : 'text';
  if (Array.isArray(value)) {
    if (value.length === 0) return 'none';
    if (value.every(isScalar)) return 'chips';
    if (value.every(isObject)) return 'records';
    return 'list';
  }
  const values = Object.values(value);
  if (values.length === 0) return 'none';
  return values.every(isObject) ? 'records' : 'fields';
}

export interface Field {
  key: string;
  value: ViewJson;
}

export interface ViewRecord {
  /** What names the record: its map key, or a naming field of its own (`name`, `id`…). */
  title: string | null;
  fields: Field[];
}

/** Fields that name the object they sit in, tried in order to title a list item. */
const TITLE_FIELDS = ['name', 'title', 'hostname', 'id', 'key'];

/** Scalars first so they line up as a compact grid, nested values after — each keeps key order. */
export function toFields(object: ViewObject): Field[] {
  const fields = Object.entries(object).map(([key, value]) => ({ key, value }));
  return [...fields.filter((f) => isScalar(f.value)), ...fields.filter((f) => !isScalar(f.value))];
}

/** A `records`-shaped value as cards: a keyed map titles each by its key, a list by a naming field. */
export function toRecords(value: ViewJson): ViewRecord[] {
  if (Array.isArray(value)) {
    return value.filter(isObject).map((item) => {
      const titleKey = TITLE_FIELDS.find(
        (k) => typeof item[k] === 'string' || typeof item[k] === 'number',
      );
      if (titleKey === undefined) return { title: null, fields: toFields(item) };
      const { [titleKey]: title, ...rest } = item;
      return { title: String(title), fields: toFields(rest) };
    });
  }
  if (isObject(value)) {
    return Object.entries(value).map(([key, item]) => ({
      title: key,
      fields: isObject(item) ? toFields(item) : [{ key: 'value', value: item }],
    }));
  }
  return [];
}

export interface LabelledValue {
  label: string;
  value: ViewJson;
}

export interface ViewTile {
  key: string;
  /** Scalar columns, shown as compact label/value pills. */
  stats: LabelledValue[];
  /** Lists and maps, each drawn full-width under the pills. */
  blocks: LabelledValue[];
}

/**
 * One collection of a view, laid out for its content: a plain table while every column is a
 * scalar (numbers right-aligned), one tile per item as soon as a column holds lists or maps
 * — chips and nested cards don't fit a table cell.
 */
export type TableLayout =
  { kind: 'table'; numeric: boolean[] } | { kind: 'tiles'; tiles: ViewTile[] };

export function layoutTable(table: ViewTableModel): TableLayout {
  const columnValues = table.columns.map((_, i) => table.rows.map((r) => r.values[i] ?? null));
  const scalar = columnValues.map((values) => values.every(isScalar));
  if (scalar.every(Boolean)) {
    return {
      kind: 'table',
      numeric: columnValues.map((values) =>
        values.every((v) => v === null || typeof v === 'number'),
      ),
    };
  }
  return {
    kind: 'tiles',
    tiles: table.rows.map((row) => {
      const cells = table.columns.map((c, i) => ({
        label: c.label,
        value: row.values[i] ?? null,
      }));
      return {
        key: row.key,
        stats: cells.filter((_, i) => scalar[i]),
        blocks: cells.filter((_, i) => !scalar[i]),
      };
    }),
  };
}

/** A view's plain (non-collection) values: scalars as headline stats, the rest as blocks. */
export function splitValues(view: CapabilityViewModel): {
  stats: LabelledValue[];
  blocks: LabelledValue[];
} {
  const values = view.values.map((v) => ({ label: v.label, value: v.value ?? null }));
  return {
    stats: values.filter((v) => isScalar(v.value)),
    blocks: values.filter((v) => !isScalar(v.value)),
  };
}
