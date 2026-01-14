import { describe, expect, it } from 'vitest';
import { screen } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { server } from '@/test/mswUtils.ts';
import { renderRoute } from '@/test/routerUtils.tsx';

describe('authenticated index route', () => {
  it('renders the PDF upload experience', async () => {
    // arrange
    let filesRequestCount = 0;
    let userRequestCount = 0;

    server.use(
      http.get('/api/user/me', () => {
        userRequestCount += 1;
        return HttpResponse.json({
          email: 'user@example.com',
          id: 'user-1',
          name: 'Test User',
          roles: [],
        });
      }),
      http.get('/api/file', () => {
        filesRequestCount += 1;
        return HttpResponse.json([]);
      })
    );

    // act
    const { cleanup } = renderRoute({ initialPath: '/' });

    // Assert the rendered output
    try {
      expect(
        await screen.findByText('PDF Accessibility Conversion Tool')
      ).toBeInTheDocument();
      expect(await screen.findByText('Upload PDF Files')).toBeInTheDocument();
      expect(await screen.findByText('No files yet.')).toBeInTheDocument();
      expect(filesRequestCount).toBe(1);
      expect(userRequestCount).toBe(1);
    } finally {
      cleanup();
    }
  });
});
