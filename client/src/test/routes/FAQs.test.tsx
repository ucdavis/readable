import { describe, expect, it, vi } from 'vitest';
import { screen, within } from '@testing-library/react';
import { renderRoute } from '@/test/routerUtils.tsx';

describe('FAQs route', () => {
  it('explains which PDFs are a good fit for remediation from the anchor link', async () => {
    const scrollIntoView = vi.fn();
    const originalScrollIntoView = HTMLElement.prototype.scrollIntoView;
    HTMLElement.prototype.scrollIntoView = scrollIntoView;

    const { cleanup } = renderRoute({
      initialEntries: ['/FAQs#pdf-fit'],
      initialPath: '/FAQs',
    });

    try {
      await screen.findByText('How it works');

      const pdfFitSection = document.getElementById('pdf-fit');
      expect(pdfFitSection).not.toBeNull();
      expect(scrollIntoView).toHaveBeenCalled();
      expect(
        pdfFitSection?.querySelector('input[type="checkbox"]')
      ).toBeChecked();

      const pdfFitContent = within(pdfFitSection!);
      expect(
        await pdfFitContent.findByText(
          'What PDFs work best, and which ones are a poor fit?'
        )
      ).toBeInTheDocument();
      expect(
        pdfFitContent.getByText(/transcribe or recreate it as real text first/i)
      ).toBeInTheDocument();
      expect(
        pdfFitContent.getByText(
          /equations, notation, or other critical content exist only as images/i
        )
      ).toBeInTheDocument();
    } finally {
      HTMLElement.prototype.scrollIntoView = originalScrollIntoView;
      cleanup();
    }
  });
});
