/** The sections of an instance page. Each one is a child route (`…/:instance/<id>`), so
 * the URL, refresh and shared links all keep the section. */
export const INSTANCE_TABS = [
  { id: 'capabilities', label: 'Capabilities', icon: 'bolt' },
  { id: 'queue', label: 'Run queue', icon: 'format_list_numbered' },
  { id: 'graph', label: 'Graph', icon: 'account_tree' },
  { id: 'inventory', label: 'Inventory', icon: 'description' },
] as const;
