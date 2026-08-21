import { Injectable } from '@angular/core';
import { FormControl, FormGroup, Validators } from '@angular/forms';

import { DynamicFormElement } from '../components/dynamic-form/types/dynamic-form-element';

/** Generic reactive-forms builder for a `DynamicFormRoot` — no dashboard-domain
 * knowledge here; that lives in `DashboardService.getInitialOptions`. */
@Injectable({
  providedIn: 'root',
})
export class QuestionControlService {
  toFormGroup(
    questions: DynamicFormElement<any>[],
    overrideValues: {
      [key: string]: any;
    } = {},
  ) {
    const group: any = {};
    questions.forEach((question) => {
      const defaultValues = overrideValues[question.key] ?? question.defaultValue;
      group[question.key] = question.required
        ? new FormControl(defaultValues, Validators.required)
        : new FormControl(defaultValues);
    });
    return new FormGroup(group);
  }
}
