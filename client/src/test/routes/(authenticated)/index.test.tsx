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
      http.get('/api/status/banner', () => {
        return new HttpResponse(null, { status: 204 });
      }),
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
      expect(
        await screen.findByText(
          /Best results come from text-based PDFs\. Scanned, handwritten, or image-only content may need manual transcription first\./
        )
      ).toBeInTheDocument();
      expect(
        screen.getByRole('link', { name: 'Learn what works best.' })
      ).toHaveAttribute('href', '/FAQs#pdf-fit');
      expect(await screen.findByText('No files yet.')).toBeInTheDocument();
      expect(filesRequestCount).toBe(1);
      expect(userRequestCount).toBe(1);
    } finally {
      cleanup();
    }
  });

  it('marks completed files without reports as unavailable', async () => {
    const fileId = '9e9273e4-52a1-4c43-81ec-d5e61f65a75d';

    server.use(
      http.get('/api/status/banner', () => {
        return new HttpResponse(null, { status: 204 });
      }),
      http.get('/api/user/me', () => {
        return HttpResponse.json({
          email: 'user@example.com',
          id: 'user-1',
          name: 'Test User',
          roles: [],
        });
      }),
      http.get('/api/file', () => {
        return HttpResponse.json([
          {
            accessibilityReports: [],
            contentType: 'application/pdf',
            createdAt: '2026-05-04T18:05:00Z',
            fileId,
            latestFailureReason: null,
            originalFileName: 'xfa.pdf',
            sizeBytes: 123,
            status: 'Completed',
            statusUpdatedAt: '2026-05-04T18:09:29Z',
          },
        ]);
      })
    );

    const { cleanup } = renderRoute({ initialPath: '/' });

    try {
      expect(await screen.findByText('Report unavailable')).toBeInTheDocument();
      expect(screen.getByText('Details in report view')).toBeInTheDocument();
      expect(screen.queryByText('No report yet')).not.toBeInTheDocument();
    } finally {
      cleanup();
    }
  });
});
