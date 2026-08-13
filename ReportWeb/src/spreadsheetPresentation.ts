export interface SpreadsheetWarningConfig {
  disableForceStringAlert: boolean;
  disableForceStringMark: boolean;
}

/** 只读共享页无法执行“文本转数字”，身份证号等长数字也必须保持文本精度。 */
export function createSpreadsheetWarningConfig(
  suppressNumericTextWarnings: boolean,
): SpreadsheetWarningConfig {
  return {
    disableForceStringAlert: suppressNumericTextWarnings,
    disableForceStringMark: suppressNumericTextWarnings,
  };
}
