import { CapabilityViewModel, ViewTableModel } from '../../../domain';
import { layoutTable, shapeOf, splitValues, toRecords } from './view-card.model';

function table(values: ViewTableModel['rows'][number]['values'][]): ViewTableModel {
  return {
    collection: 'proxmox.virtual_machines',
    columns: values[0].map((_, i) => ({ label: `c${i}`, field: `f${i}` })),
    rows: values.map((v, i) => ({ key: `vm${i}`, values: v })),
  };
}

describe('shapeOf', () => {
  it('tells scalars apart', () => {
    expect(shapeOf(null)).toBe('empty');
    expect(shapeOf(true)).toBe('boolean');
    expect(shapeOf(4)).toBe('number');
    expect(shapeOf('pve')).toBe('text');
    expect(shapeOf('https://example.com/x')).toBe('link');
  });

  it('draws a list of scalars as chips, of objects as records, mixed as a list', () => {
    expect(shapeOf(['a.yml', 'b.yml'])).toBe('chips');
    expect(shapeOf([{ size: 10 }])).toBe('records');
    expect(shapeOf(['a', { size: 10 }])).toBe('list');
    expect(shapeOf([])).toBe('none');
  });

  it('reads a map of maps as a keyed collection, any other map as fields', () => {
    expect(shapeOf({ a: { ttl: 1 }, b: { ttl: 2 } })).toBe('records');
    expect(shapeOf({ ttl: 1, records: { a: {} } })).toBe('fields');
    expect(shapeOf({})).toBe('none');
  });
});

describe('toRecords', () => {
  it('titles a list item by its naming field and drops it from the fields', () => {
    const [record] = toRecords([{ type: 'CNAME', name: 'test.dev', ttl: 1 }]);
    expect(record.title).toBe('test.dev');
    expect(record.fields.map((f) => f.key)).toEqual(['type', 'ttl']);
  });

  it('titles a keyed map entry by its key, scalars before nested values', () => {
    const [record] = toRecords({ web: { ports: [80], image: 'nginx' } });
    expect(record.title).toBe('web');
    expect(record.fields.map((f) => f.key)).toEqual(['image', 'ports']);
  });
});

describe('layoutTable', () => {
  it('keeps a table while every column is scalar, flagging numeric columns', () => {
    const layout = layoutTable(
      table([
        [4, 'x'],
        [null, 'y'],
      ]),
    );
    expect(layout).toEqual({ kind: 'table', numeric: [true, false] });
  });

  it('switches to tiles once a column holds lists, splitting stats from blocks', () => {
    const layout = layoutTable(
      table([
        [4, ['a.yml']],
        [2, null],
      ]),
    );
    expect(layout.kind).toBe('tiles');
    if (layout.kind !== 'tiles') return;
    expect(layout.tiles[0]).toEqual({
      key: 'vm0',
      stats: [{ label: 'c0', value: 4 }],
      blocks: [{ label: 'c1', value: ['a.yml'] }],
    });
    expect(layout.tiles[1].blocks).toEqual([{ label: 'c1', value: null }]);
  });
});

describe('splitValues', () => {
  it('puts scalars in the stats and the rest in blocks', () => {
    const view: CapabilityViewModel = {
      code: 'v',
      title: 'V',
      description: '',
      tables: [],
      values: [
        { label: 'node', path: 'proxmox.node', value: 'pve' },
        { label: 'tags', path: 'tags', value: ['a'] },
      ],
    };
    const { stats, blocks } = splitValues(view);
    expect(stats.map((s) => s.label)).toEqual(['node']);
    expect(blocks.map((b) => b.label)).toEqual(['tags']);
  });
});
