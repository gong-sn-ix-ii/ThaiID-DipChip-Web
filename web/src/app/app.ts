import { Component } from '@angular/core';
import { ThaiIdReader } from './thai-id/thai-id-reader';

@Component({
  selector: 'app-root',
  imports: [ThaiIdReader],
  templateUrl: './app.html',
  styleUrl: './app.css',
})
export class App {}
