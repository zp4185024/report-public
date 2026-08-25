export interface WorksheetDataProtectionPoints<TPoint> {
  deleteProtection: TPoint;
  filter: TPoint;
  setCellValue: TPoint;
}

export interface WorksheetPermissionLike<TPoint> {
  isProtected(): boolean;
  protect(options: { allowViewByOthers: boolean; name: string }): Promise<unknown>;
  setEditable(): Promise<void>;
  setPoint(point: TPoint, allowed: boolean): Promise<void>;
}

export interface WorksheetProtectionShadowController {
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
