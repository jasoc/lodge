import {
  findActiveNavigation,
  NavigationElement,
  navigationElementsTree,
  rememberedRedirect,
} from './navigation-tree';

describe('navigation tree', () => {
  const kind = new NavigationElement({
    name: 'VMs',
    redirect: 'instances/vm',
    subElements: [
      new NavigationElement({ name: 'Overview', redirect: 'instances/vm' }),
      new NavigationElement({ name: 'web', redirect: 'instances/vm/web' }),
    ],
  });
  const tree = [...navigationElementsTree, kind];

  it('matches the longest redirect prefix, ignoring query and slashes', () => {
    expect(findActiveNavigation(tree, '/home/about?x=1#y')?.leaf.name).toBe('About');
    expect(findActiveNavigation(tree, '/home')?.leaf.name).toBe('Overview');
    expect(findActiveNavigation(tree, '/instances/vm/web/')?.leaf.name).toBe('web');
    expect(findActiveNavigation(tree, '/instances/vm/web/x/y')?.parent).toBe(kind);
    expect(findActiveNavigation(tree, '/instances/vm')?.leaf.name).toBe('Overview');
  });

  it('matches top-level entries without a parent, and nothing for unknown urls', () => {
    const drift = findActiveNavigation(tree, '/drift');
    expect(drift?.leaf.name).toBe('Drift');
    expect(drift?.parent).toBeNull();
    expect(findActiveNavigation(tree, '/nowhere')).toBeNull();
    expect(findActiveNavigation(tree, '/driftwood')).toBeNull();
  });

  it('remembers the last child, falling back to the module root', () => {
    expect(rememberedRedirect(kind, { VMs: 'instances/vm/web' })).toBe('instances/vm/web');
    expect(rememberedRedirect(kind, {})).toBe('instances/vm');
    expect(rememberedRedirect(kind, { VMs: 'instances/vm/deleted' })).toBe('instances/vm');
  });
});
