import { describe, expect, it } from 'vitest';
import { screen } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { server } from '@/test/mswUtils.ts';
import { renderRoute } from '@/test/routerUtils.tsx';

describe('report route', () => {
  it('explains when an XFA PDF cannot be checked', async () => {
    const fileId = '6b45b501-2b20-4f55-8491-73cf55747a61';

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
      http.get(`/api/file/${fileId}`, () => {
        return HttpResponse.json({
          accessibilityReports: [],
          accessibilityReportWarnings: [
            {
              code: 'XfaUnsupported',
              message:
                'The Adobe accessibility checker cannot analyze XFA form PDFs.',
              stage: 'After',
            },
          ],
          contentType: 'application/pdf',
          createdAt: '2026-05-04T18:05:00Z',
          fileId,
          latestFailureReason: null,
          originalFileName: 'xfa.pdf',
          sizeBytes: 123,
          status: 'Completed',
          statusUpdatedAt: '2026-05-04T18:09:29Z',
        });
      })
    );

    const { cleanup } = renderRoute({ initialPath: `/reports/${fileId}` });

    try {
      expect(
        await screen.findByText(
          /Readable processed this PDF, but an accessibility report could not be generated because the Adobe accessibility checker cannot analyze XFA form PDFs\./
        )
      ).toBeInTheDocument();

      expect(
        screen.getByRole('link', { name: 'Learn more about XFA PDF forms.' })
      ).toHaveAttribute(
        'href',
        'https://experienceleague.adobe.com/en/docs/experience-manager-learn/forms/document-services/pdf-forms-and-documents'
      );
      expect(
        screen.queryByText('No accessibility reports found for this file yet.')
      ).not.toBeInTheDocument();
    } finally {
      cleanup();
    }
  });
});
