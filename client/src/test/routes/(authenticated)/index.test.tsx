import { describe, expect, it } from 'vitest';
import { screen } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { server } from '@/test/mswUtils.ts';
import { renderRoute } from '@/test/routerUtils.tsx';

describe('authenticated index route', () => {
  it('renders the latest processing failure reason for failed files', async () => {
    server.use(
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
            createdAt: '2026-03-11T00:00:00Z',
            fileId: 'file-1',
            originalFileName: 'too-many-pages.pdf',
            processingErrorMessage:
              'PDFs are temporarily limited to 25 pages. This file has 26 pages.',
            sizeBytes: 1024,
            status: 'Failed',
            statusUpdatedAt: '2026-03-11T00:01:00Z',
          },
        ]);
      })
    );

    const { cleanup } = renderRoute({ initialPath: '/' });

    try {
      const failedBadge = await screen.findByText('Failed');
      expect(failedBadge).toHaveAttribute(
        'title',
        'PDFs are temporarily limited to 25 pages. This file has 26 pages.'
      );
      expect(failedBadge).toHaveAttribute(
        'aria-label',
        'Failure reason: PDFs are temporarily limited to 25 pages. This file has 26 pages.'
      );
      expect(await screen.findByText('Processing failed')).toBeInTheDocument();
    } finally {
      cleanup();
    }
  });
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
});



