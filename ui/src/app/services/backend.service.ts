import { firstValueFrom } from 'rxjs';

import { HttpClient, HttpParams, HttpResponse } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';

/**
 * Thin HTTP wrapper over Lodge's API. Always same-origin: in production Lodge.Server
 * serves the built SPA itself, and in dev `ng serve`'s proxy config forwards `/api` to
 * it (see proxy.conf.json) — so there's never a second origin to resolve or a CORS
 * policy to configure. Lodge's minimal-API endpoints return plain JSON bodies, unlike
 * clip's `{success,message,data}` envelope, so there's nothing to unwrap here either.
 * Auth is handled entirely by `authInterceptor`, not by this service.
 */
const API_BASE_URL = '/api/v1/';

@Injectable()
export class BackendService {
  protected readonly httpClient = inject(HttpClient);

  private async sendRequest<ReturnType>(
    method: string,
    relativeUrl: string,
    body: any = null,
    params?: any,
  ): Promise<HttpResponse<ReturnType>> {
    if (relativeUrl.startsWith('/')) {
      relativeUrl = relativeUrl.slice(1);
    }

    let httpPar = new HttpParams();
    if (params) {
      Object.keys(params).forEach((key) => {
        httpPar = httpPar.append(key, params[key]);
      });
    }

    return firstValueFrom(
      this.httpClient.request<ReturnType>(method, API_BASE_URL + relativeUrl, {
        body,
        params: httpPar,
        observe: 'response',
      }),
    );
  }

  public async post<ReturnType>(
    relativeUrl: string,
    body: any = {},
  ): Promise<HttpResponse<ReturnType>> {
    return this.sendRequest('POST', relativeUrl, body);
  }

  public async get<ReturnType>(
    relativeUrl: string,
    params?: any,
  ): Promise<HttpResponse<ReturnType>> {
    return this.sendRequest('GET', relativeUrl, null, params);
  }

  public async put<ReturnType>(relativeUrl: string, body: any): Promise<HttpResponse<ReturnType>> {
    return this.sendRequest('PUT', relativeUrl, body);
  }

  public async delete<ReturnType>(relativeUrl: string): Promise<HttpResponse<ReturnType>> {
    return this.sendRequest('DELETE', relativeUrl);
  }
}
