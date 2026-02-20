import { useEffect } from 'react';
import { useRouter } from '@tanstack/react-router';
import { trackPageView } from '@/analytics.ts';

export function AnalyticsListener() {
  const router = useRouter();

  useEffect(() => {
    trackPageView();

    const unsubscribe = router.subscribe('onResolved', () => {
      trackPageView();
    });

    return unsubscribe;
  }, [router]);

  return null;
}
