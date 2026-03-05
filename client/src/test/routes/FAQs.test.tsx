import { describe, expect, it } from 'vitest';
import { screen } from '@testing-library/react';
import { renderRoute } from '@/test/routerUtils.tsx';

describe('FAQs route', () => {
  it('explains which PDFs are a good fit for remediation', async () => {
    const { cleanup } = renderRoute({ initialPath: '/FAQs' });

    try {
      expect(
        await screen.findByText(
          'What PDFs work best, and which ones are a poor fit?'
        )
      ).toBeInTheDocument();
      expect(
        screen.getByText(/transcribe or recreate it as real text first/i)
      ).toBeInTheDocument();
      expect(
        screen.getByText(
          /equations, notation, or other critical content exist only as images/i
        )
      ).toBeInTheDocument();
    } finally {
      cleanup();
    }
  });
});
