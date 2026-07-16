"""将当前 SQLite 验证库中的单个报表模板迁移到 SQL Server。

该工具故意不做通用数据库搬迁：只读取 RcPocReportTemplate，只写入
hd_report_center.dbo.RcReportTemplate，从而避免将验证日志和旧的 Admin.NET 状态带入新库。
"""

from __future__ import annotations

import argparse
import hashlib
import os
import shutil
import sqlite3
import sys
from contextlib import closing
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Protocol, Sequence


EXPECTED_TARGET_DATABASE = "hd_report_center"
SOURCE_TABLE = "RcPocReportTemplate"
TARGET_TABLE = "RcReportTemplate"


class MigrationError(RuntimeError):
    """表示可向操作人员直接说明的迁移校验或执行错误。"""


class ScalarCursor(Protocol):
    """迁移逻辑所依赖的最小 DB-API cursor 接口。"""

    def execute(self, command: str, parameters: Sequence[object] = ()) -> Any: ...

    def fetchone(self) -> Sequence[Any] | None: ...


@dataclass(frozen=True)
class SourceTemplate:
    """与源表列一一对应，包含 EntityBase 的审计字段。"""

    id: int
    report_code: str
    snapshot_json: str
    binding_json: str
    revision: int
    create_time: datetime | None
    update_time: datetime | None
    create_user_id: int | None
    create_user_name: str | None
    update_user_id: int | None
    update_user_name: str | None

    def insert_values(self) -> tuple[object, ...]:
        """按目标 INSERT 列顺序返回参数，禁止用字符串拼接 JSON 和审计值。"""

        return (
            self.id,
            self.report_code,
            self.snapshot_json,
            self.binding_json,
            self.revision,
            self.create_time,
            self.update_time,
            self.create_user_id,
            self.create_user_name,
            self.update_user_id,
            self.update_user_name,
        )


@dataclass(frozen=True)
class MigrationResult:
    report_code: str
    revision: int
    snapshot_length: int
    binding_length: int
    backup_path: Path


def validate_target_database(database: str) -> None:
    """防止命令误指向 lis_admin 或其它现有库。"""

    if database != EXPECTED_TARGET_DATABASE:
        raise MigrationError(f"目标数据库必须是 {EXPECTED_TARGET_DATABASE}。")


def _parse_sqlite_datetime(value: object, column_name: str) -> datetime | None:
    """将 SQLite 的 ISO 时间文本转为 DB-API 可结构化传输的 datetime 参数。"""

    if value is None:
        return None
    if isinstance(value, datetime):
        return value

    try:
        # fromisoformat 会将 SQLite/.NET 输出的第 7 位小数截到 Python 的微秒精度。
        # SQL Server datetime 本身只保留毫秒级精度，由驱动在写入时完成最终舍入。
        return datetime.fromisoformat(str(value))
    except ValueError as exception:
        raise MigrationError(f"源模板的 {column_name} 不是有效时间：{value!r}") from exception


def load_source_template(source_sqlite: Path, report_code: str) -> SourceTemplate:
    """读取指定模板，并拒绝缺失或重复的源数据。"""

    source_sqlite = Path(source_sqlite)
    if not source_sqlite.is_file():
        raise MigrationError(f"SQLite 源文件不存在：{source_sqlite}")

    try:
        # sqlite3.Connection.__exit__ 不会关闭连接；显式 closing 避免 Windows 锁住源库和备份。
        with closing(sqlite3.connect(source_sqlite)) as connection:
            connection.row_factory = sqlite3.Row
            rows = connection.execute(
                f"""
                SELECT
                    Id, ReportCode, SnapshotJson, BindingJson, Revision,
                    CreateTime, UpdateTime, CreateUserId, CreateUserName,
                    UpdateUserId, UpdateUserName
                FROM {SOURCE_TABLE}
                WHERE ReportCode = ?
                LIMIT 2
                """,
                (report_code,),
            ).fetchall()
    except sqlite3.Error as exception:
        raise MigrationError(f"读取 SQLite 源模板失败：{exception}") from exception

    if len(rows) != 1:
        raise MigrationError(
            f"源表 {SOURCE_TABLE} 中 ReportCode={report_code!r} 必须恰好一条，"
            f"实际读到 {len(rows)} 条。"
        )

    row = rows[0]
    return SourceTemplate(
        id=int(row["Id"]),
        report_code=str(row["ReportCode"]),
        snapshot_json=str(row["SnapshotJson"]),
        binding_json=str(row["BindingJson"]),
        revision=int(row["Revision"]),
        create_time=_parse_sqlite_datetime(row["CreateTime"], "CreateTime"),
        update_time=_parse_sqlite_datetime(row["UpdateTime"], "UpdateTime"),
        create_user_id=row["CreateUserId"],
        create_user_name=row["CreateUserName"],
        update_user_id=row["UpdateUserId"],
        update_user_name=row["UpdateUserName"],
    )


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def create_source_backup(source_sqlite: Path, backup_directory: Path) -> Path:
    """在写目标库前制作字节级备份，并立即校验 SHA-256。"""

    source_sqlite = Path(source_sqlite).resolve()
    backup_directory = Path(backup_directory).resolve()
    if not source_sqlite.is_file():
        raise MigrationError(f"SQLite 源文件不存在：{source_sqlite}")

    backup_directory.mkdir(parents=True, exist_ok=True)
    timestamp = datetime.now(timezone.utc).strftime("%Y%m%d-%H%M%S-%fZ")
    backup_path = backup_directory / f"{source_sqlite.stem}-{timestamp}{source_sqlite.suffix}"
    shutil.copy2(source_sqlite, backup_path)

    source_hash = _sha256(source_sqlite)
    backup_hash = _sha256(backup_path)
    if source_hash != backup_hash:
        backup_path.unlink(missing_ok=True)
        raise MigrationError("SQLite 备份的 SHA-256 与源文件不一致，已中止迁移。")

    return backup_path


def ensure_target_has_no_template(cursor: ScalarCursor, report_code: str) -> None:
    """目标已有同编码时拒绝覆盖，后续同步应由应用修订号机制处理。"""

    cursor.execute(
        f"SELECT COUNT_BIG(1) FROM dbo.{TARGET_TABLE} WHERE ReportCode = %s",
        (report_code,),
    )
    row = cursor.fetchone()
    existing_count = 0 if row is None else int(row[0])
    if existing_count != 0:
        raise MigrationError(
            f"目标表 {TARGET_TABLE} 已存在 ReportCode={report_code!r}，拒绝覆盖。"
        )


def migrate_template(
    *,
    source_sqlite: Path,
    backup_directory: Path,
    server: str,
    port: int,
    database: str,
    user: str,
    password: str,
    report_code: str,
) -> MigrationResult:
    """备份源库后，在单个 SQL Server 事务内写入并核对模板。"""

    validate_target_database(database)
    if not password:
        raise MigrationError("未设置 HD_REPORT_CENTER_DB_PASSWORD。")

    template = load_source_template(source_sqlite, report_code)
    backup_path = create_source_backup(source_sqlite, backup_directory)

    try:
        import pymssql
    except ImportError as exception:
        raise MigrationError("未安装 pymssql，无法连接 SQL Server。") from exception

    connection = None
    try:
        connection = pymssql.connect(
            server=server,
            port=port,
            user=user,
            password=password,
            database=database,
            charset="UTF-8",
            login_timeout=15,
            timeout=120,
            autocommit=False,
        )
        cursor = connection.cursor()
        cursor.execute(
            "SELECT COUNT_BIG(1) FROM sys.tables WHERE name = %s",
            (TARGET_TABLE,),
        )
        table_row = cursor.fetchone()
        if table_row is None or int(table_row[0]) != 1:
            raise MigrationError(f"目标表 dbo.{TARGET_TABLE} 不存在，请先运行建库脚本。")

        ensure_target_has_no_template(cursor, report_code)
        cursor.execute(
            f"""
            INSERT INTO dbo.{TARGET_TABLE} (
                Id, ReportCode, SnapshotJson, BindingJson, Revision,
                CreateTime, UpdateTime, CreateUserId, CreateUserName,
                UpdateUserId, UpdateUserName
            ) VALUES (%s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s)
            """,
            template.insert_values(),
        )
        cursor.execute(
            f"""
            SELECT Revision, LEN(SnapshotJson), LEN(BindingJson)
            FROM dbo.{TARGET_TABLE}
            WHERE ReportCode = %s
            """,
            (report_code,),
        )
        verification = cursor.fetchone()
        expected = (template.revision, len(template.snapshot_json), len(template.binding_json))
        actual = None if verification is None else tuple(int(value) for value in verification)
        if actual != expected:
            raise MigrationError(f"目标写后校验失败：期望 {expected}，实际 {actual}。")

        connection.commit()
        return MigrationResult(
            report_code=template.report_code,
            revision=template.revision,
            snapshot_length=len(template.snapshot_json),
            binding_length=len(template.binding_json),
            backup_path=backup_path,
        )
    except MigrationError:
        if connection is not None:
            connection.rollback()
        raise
    except Exception as exception:
        if connection is not None:
            connection.rollback()
        raise MigrationError(f"写入 SQL Server 失败：{exception}") from exception
    finally:
        if connection is not None:
            connection.close()


def _build_argument_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="迁移当前 SQLite 报表模板到 SQL Server。")
    parser.add_argument("--source-sqlite", type=Path, required=True, help="SQLite 源库路径。")
    parser.add_argument("--backup-directory", type=Path, help="备份目录，默认为源库同级 backups。")
    parser.add_argument("--server", default="192.168.2.5", help="SQL Server 地址。")
    parser.add_argument("--port", type=int, default=1433, help="SQL Server 端口。")
    parser.add_argument("--database", default=EXPECTED_TARGET_DATABASE, help="目标库，必须是 hd_report_center。")
    parser.add_argument("--user", default="hdhis", help="SQL Server 用户。")
    parser.add_argument("--report-code", default="dept-workload", help="要迁移的报表编码。")
    return parser


def main() -> int:
    arguments = _build_argument_parser().parse_args()
    backup_directory = arguments.backup_directory or arguments.source_sqlite.parent / "backups"
    password = os.environ.get("HD_REPORT_CENTER_DB_PASSWORD", "")

    try:
        result = migrate_template(
            source_sqlite=arguments.source_sqlite,
            backup_directory=backup_directory,
            server=arguments.server,
            port=arguments.port,
            database=arguments.database,
            user=arguments.user,
            password=password,
            report_code=arguments.report_code,
        )
    except MigrationError as exception:
        print(f"迁移失败：{exception}", file=sys.stderr)
        return 1

    print(
        "模板迁移完成："
        f"ReportCode={result.report_code}，Revision={result.revision}，"
        f"SnapshotJson={result.snapshot_length} 字符，"
        f"BindingJson={result.binding_length} 字符。"
    )
    print(f"SQLite 备份：{result.backup_path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
