import { describe, expect, it } from 'vitest';

import { adobeAccessibilityRuleHref } from '@/shared/accessibility/ruleInfoLink.tsx';

const BASE_URL =
  'https://helpx.adobe.com/acrobat/using/create-verify-pdf-accessibility.html';

describe('adobeAccessibilityRuleHref', () => {
  it('maps known Adobe Accessibility Checker rules to anchors', () => {
    const cases: Array<[rule: string, anchor: string]> = [
      ['Accessibility permission flag', 'Perms'],
      ['Appropriate nesting', 'Headings'],
      ['Associated with content', 'AltTextNoContent'],
      ['Bookmarks', 'Bookmarks'],
      ['Character encoding', 'CharEnc'],
      ['Color contrast', 'ColorContrast'],
      ['Field descriptions', 'FormFieldNames'],
      ['Figures alternate text', 'FigAltText'],
      ['Headers', 'TableHeaders'],
      ['Hides annotation', 'HiddenAnnot'],
      ['Image-only PDF', 'ImageOnlyPDF'],
      ['List items', 'ListItems'],
      ['Lbl and LBody', 'LblLBody'],
      ['Logical reading order', 'LogicalRO'],
      ['Navigation links', 'NavLinks'],
      ['Nested alternate text', 'NestedAltText'],
      ['Other elements alternate text', 'OtherAltText'],
      ['Permissions', 'Perms'],
      ['Primary language', 'PrimeLang'],
      ['Regularity', 'RegularTable'],
      ['Rows', 'TableRows'],
      ['Screen flicker', 'FlickerRate'],
      ['Scripts', 'Scripts'],
      ['Summary', 'TableSummary'],
      ['TH and TD', 'THTD'],
      ['Tab order', 'TabOrder'],
      ['Tagged annotations', 'TaggedAnnots'],
      ['Tagged content', 'TaggedCont'],
      ['Tagged form fields', 'TaggedFormFields'],
      ['Tagged multimedia', 'Multimedia'],
      ['Tagged PDF', 'TaggedPDF'],
      ['Timed responses', 'TimedResponses'],
      ['Title', 'DocTitle'],
    ];

    for (const [rule, anchor] of cases) {
      expect(adobeAccessibilityRuleHref(rule)).toBe(`${BASE_URL}#${anchor}`);
    }
  });

  it('falls back to the base URL when a rule is unknown', () => {
    expect(adobeAccessibilityRuleHref('Not a real rule')).toBe(BASE_URL);
  });
});
