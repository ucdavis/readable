import { InformationCircleIcon } from '@heroicons/react/24/outline';

const ADOBE_ACCESSIBILITY_RULES_URL =
  'https://helpx.adobe.com/acrobat/using/create-verify-pdf-accessibility.html';

const ADOBE_ACCESSIBILITY_RULE_ANCHORS = [
  'Perms',
  'ImageOnlyPDF',
  'TaggedPDF',
  'LogicalRO',
  'PrimeLang',
  'DocTitle',
  'Bookmarks',
  'ColorContrast',
  'TaggedCont',
  'TaggedAnnots',
  'TabOrder',
  'CharEnc',
  'Multimedia',
  'FlickerRate',
  'Scripts',
  'TimedResponses',
  'NavLinks',
  'TaggedFormFields',
  'FormFieldNames',
  'FigAltText',
  'NestedAltText',
  'AltTextNoContent',
  'HiddenAnnot',
  'OtherAltText',
  'TableRows',
  'THTD',
  'TableHeaders',
  'RegularTable',
  'TableSummary',
  'ListItems',
  'LblLBody',
  'Headings',
] as const;

type AdobeAccessibilityRuleAnchor =
  (typeof ADOBE_ACCESSIBILITY_RULE_ANCHORS)[number];

function normalizeRuleKey(value: string) {
  return value
    .trim()
    .toLowerCase()
    .replaceAll(/[^\da-z]+/g, '');
}

const ADOBE_ACCESSIBILITY_RULE_ANCHORS_BY_KEY = Object.fromEntries(
  ADOBE_ACCESSIBILITY_RULE_ANCHORS.map((anchor) => [
    normalizeRuleKey(anchor),
    anchor,
  ])
) as Readonly<Record<string, AdobeAccessibilityRuleAnchor>>;

const RULE_KEY_TO_ADOBE_ANCHOR: Readonly<
  Record<string, AdobeAccessibilityRuleAnchor>
> = {
  accessibilitypermissionflag: 'Perms',
  appropriatenesting: 'Headings',
  associatedwithcontent: 'AltTextNoContent',
  characterencoding: 'CharEnc',
  fielddescriptions: 'FormFieldNames',
  figuresalternatetext: 'FigAltText',
  headers: 'TableHeaders',
  hiddenannotation: 'HiddenAnnot',
  hidesannotation: 'HiddenAnnot',
  lblandlbody: 'LblLBody',
  logicalreadingorder: 'LogicalRO',
  navigationlinks: 'NavLinks',
  nestedalternatetext: 'NestedAltText',
  otherelementsalternatetext: 'OtherAltText',
  permissions: 'Perms',
  primarylanguage: 'PrimeLang',
  regularity: 'RegularTable',
  rows: 'TableRows',
  screenflicker: 'FlickerRate',
  summary: 'TableSummary',
  taggedannotations: 'TaggedAnnots',
  taggedcontent: 'TaggedCont',
  taggedmultimedia: 'Multimedia',
  thandtd: 'THTD',
  title: 'DocTitle',
};

function ruleToAdobeAnchor(rule: string) {
  const ruleKey = normalizeRuleKey(rule);
  if (!ruleKey) {
    return null;
  }

  return (
    RULE_KEY_TO_ADOBE_ANCHOR[ruleKey] ??
    ADOBE_ACCESSIBILITY_RULE_ANCHORS_BY_KEY[ruleKey] ??
    null
  );
}

export function adobeAccessibilityRuleHref(rule: string) {
  const anchor = ruleToAdobeAnchor(rule);
  if (!anchor) {
    return ADOBE_ACCESSIBILITY_RULES_URL;
  }
  return `${ADOBE_ACCESSIBILITY_RULES_URL}#${encodeURIComponent(anchor)}`;
}

export type RuleInfoLinkProps = {
  className?: string;
  rule: string;
};

export function RuleInfoLink({ className, rule }: RuleInfoLinkProps) {
  return (
    <a
      aria-label={`Open Adobe documentation for rule: ${rule}`}
      className={`btn btn-ghost btn-xs btn-circle ${className ?? ''}`.trim()}
      href={adobeAccessibilityRuleHref(rule)}
      rel="noreferrer noopener"
      target="_blank"
      title="Open Adobe documentation for this rule"
    >
      <InformationCircleIcon className="h-4 w-4" />
    </a>
  );
}
