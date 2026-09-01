export interface WorksheetDataProtectionPoints<TPoint> {
  deleteProtection: TPoint;
  filter: TPoint;
  setCellValue: TPoint;
}

export interface WorksheetPermissionLike<TPoint> {
  /**
   * 校验 worksheetDataProtection 的 is Protected 条件，并返回可供后续流程判断的结果。
   */
  isProtected(): boolean;
  /**
   * 负责 worksheetDataProtection 的 protect 处理：读取该步骤所需数据，完成对应状态变更并返回结果。
   * @param options - 控制本次处理行为的配置选项。
   */
  protect(options: { allowViewByOthers: boolean; name: string }): Promise<unknown>;
  /**
   * 更新 worksheetDataProtection 的 set Editable 状态，并同步相关业务数据。
   */
  setEditable(): Promise<void>;
  /**
   * 更新 worksheetDataProtection 的 set Point 状态，并同步相关业务数据。
   * @param point - point 对应的业务输入；具体类型和空值范围以方法签名为准。
   * @param allowed - allowed 对应的业务输入；具体类型和空值范围以方法签名为准。
   */
  setPoint(point: TPoint, allowed: boolean): Promise<void>;
}

export interface WorksheetProtectionShadowController {
  /**
   * 更新 worksheetDataProtection 的 set Protected Range Shadow Strategy 状态，并同步相关业务数据。
   * @param strategy - strategy 对应的业务输入；具体类型和空值范围以方法签名为准。
   */
  setProtectedRangeShadowStrategy(
    strategy: 'always' | 'non-editable' | 'non-viewable' | 'none',
  ): void;
}

/** 保护权限继续生效时，只控制 Univer 是否绘制斜纹提示。 */
export function setWorksheetProtectionShadowVisible(
  controller: WorksheetProtectionShadowController,
  visible: boolean,
): void {
  controller.setProtectedRangeShadowStrategy(visible ? 'always' : 'none');
}

/**
 * Univer 的工作表保护会默认关闭所有编辑能力。先恢复编辑权限，
 * 再单独禁用值写入，才能让字体、颜色、边框和对齐工具继续可用。
 */
export async function configureWorksheetDataProtection<TPoint>(
  permission: WorksheetPermissionLike<TPoint>,
  protectedMode: boolean,
  points: WorksheetDataProtectionPoints<TPoint>,
  protectionName: string,
): Promise<boolean> {
  let createdProtection = false;
  if (!permission.isProtected()) {
    if (!protectedMode) return false;
    await permission.protect({
      allowViewByOthers: true,
      name: protectionName,
    });
    createdProtection = true;
  }

  await permission.setEditable();
  if (protectedMode) {
    await permission.setPoint(points.filter, true);
    await permission.setPoint(points.setCellValue, false);
    await permission.setPoint(points.deleteProtection, false);
  }
  return createdProtection;
}
