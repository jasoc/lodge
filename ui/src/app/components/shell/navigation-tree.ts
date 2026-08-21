export class NavigationElement {
  public subElements: NavigationElement[] = [];
  public name: string = '';
  public icon: string = '';
  public redirect: string = '';
  public rippled: boolean = false;
  public isExpanded: boolean = false;
  public type: 'button' | 'tag' = 'button';
  public constructor(init?: Partial<NavigationElement>) {
    Object.assign(this, init);
  }

  public clone(): NavigationElement {
    const cloned = new NavigationElement({
      name: this.name,
      icon: this.icon,
      redirect: this.redirect,
      rippled: this.rippled,
      isExpanded: this.isExpanded,
      type: this.type,
    });
    cloned.subElements = this.subElements.map((sub) => sub.clone());
    return cloned;
  }
}

export const navigationElementsTree: NavigationElement[] = [
  new NavigationElement({
    name: 'Home',
    icon: 'token',
    redirect: 'home',
    subElements: [
      new NavigationElement({
        name: 'Overview',
        icon: 'navigate_next',
        redirect: 'home',
      }),
      new NavigationElement({
        name: 'About',
        icon: 'info',
        redirect: 'home/about',
      }),
    ],
  }),
  new NavigationElement({
    type: 'tag',
    name: 'Lodge',
    icon: 'token',
  }),
  new NavigationElement({
    name: 'Dashboards',
    icon: 'dataset',
    redirect: 'dashboards',
  }),
  new NavigationElement({
    name: 'Settings',
    icon: 'settings',
    redirect: 'settings',
  }),
];
