import { ChangeDetectionStrategy, Component } from '@angular/core';
import { MatIconModule } from '@angular/material/icon';

@Component({
  selector: 'lodge-home-about',
  standalone: true,
  templateUrl: './home-about.component.html',
  styleUrls: ['./home-about.component.scss'],
  imports: [MatIconModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class HomeAboutComponent {}
