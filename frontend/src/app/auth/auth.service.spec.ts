import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { AuthService } from './auth.service';

describe('AuthService', () => {
  let service: AuthService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    service = TestBed.inject(AuthService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('keeps the access token in memory only after login', async () => {
    const login = service.login('pm.uno', 'segreta');
    const request = http.expectOne('/api/auth/login');
    expect(request.request.body).toEqual({ username: 'pm.uno', password: 'segreta' });
    request.flush({ accessToken: 'jwt-1', expiresAt: '2030-01-01T00:00:00Z' });
    await login;

    expect(service.accessToken()).toBe('jwt-1');
    expect(service.isAuthenticated()).toBe(true);
    expect(JSON.stringify({ ...localStorage, ...sessionStorage })).not.toContain('jwt-1');
  });

  it('clears the token when refresh fails', async () => {
    const login = service.login('pm.uno', 'segreta');
    http.expectOne('/api/auth/login').flush({ accessToken: 'jwt-1', expiresAt: '2030-01-01T00:00:00Z' });
    await login;

    const refreshed = service.refresh();
    http.expectOne('/api/auth/refresh').flush(null, { status: 401, statusText: 'Unauthorized' });

    expect(await refreshed).toBe(false);
    expect(service.isAuthenticated()).toBe(false);
  });

  it('shares a single request between concurrent refreshes', async () => {
    const first = service.refresh();
    const second = service.refresh();
    http.expectOne('/api/auth/refresh').flush({ accessToken: 'jwt-2', expiresAt: '2030-01-01T00:00:00Z' });

    expect(await first).toBe(true);
    expect(await second).toBe(true);
    expect(service.accessToken()).toBe('jwt-2');
  });

  it('clears the token on logout even if the server call fails', async () => {
    const login = service.login('pm.uno', 'segreta');
    http.expectOne('/api/auth/login').flush({ accessToken: 'jwt-1', expiresAt: '2030-01-01T00:00:00Z' });
    await login;

    const logout = service.logout();
    http.expectOne('/api/auth/logout').flush(null, { status: 500, statusText: 'Server Error' });

    await expect(logout).rejects.toBeTruthy();
    expect(service.isAuthenticated()).toBe(false);
  });
});
