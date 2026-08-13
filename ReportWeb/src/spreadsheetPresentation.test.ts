import { describe, expect, it } from 'vitest';
import { createSpreadsheetWarningConfig } from './spreadsheetPresentation';

describe('createSpreadsheetWarningConfig', () => {
  it('suppresses numeric-text marks and alerts together in read-only shared views', () => {
    expect(createSpreadsheetWarningConfig(true)).toEqual({
      disableForceStringAlert: true,
      disableForceStringMark: true,
    });
  });

  it('keeps numeric-text diagnostics available in the template designer', () => {
    expect(createSpreadsheetWarningConfig(false)).toEqual({
      disableForceStringAlert: false,
      disableForceStringMark: false,
    });
  });
});
