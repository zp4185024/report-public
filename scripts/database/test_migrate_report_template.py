import hashlib
import sqlite3
import tempfile
import unittest
from contextlib import closing
from pathlib import Path

from scripts.database.migrate_report_template import (
    MigrationError,
    create_source_backup,
    ensure_target_has_no_template,
    load_source_template,
    validate_target_database,
)


class _ScalarCursor:
    """只模拟目标库重复检查所需的最小 cursor 协议。"""

    def __init__(self, scalar: int) -> None:
        self.scalar = scalar
        self.command = ""
        self.parameters: tuple[object, ...] = ()

    def execute(self, command: str, parameters: tuple[object, ...]) -> None:
        self.command = command
        self.parameters = parameters

    def fetchone(self) -> tuple[int]:
        return (self.scalar,)


class ReportTemplateMigrationTests(unittest.TestCase):
    def setUp(self) -> None:
        self._temporary_directory = tempfile.TemporaryDirectory()
        self.addCleanup(self._temporary_directory.cleanup)
        self.root = Path(self._temporary_directory.name)
        self.sqlite_path = self.root / "Admin.NET.poc.db"
        self.backup_directory = self.root / "backups"

    def _create_source(self, report_codes: list[str]) -> None:
        # sqlite3.Connection 的 with 只管理事务，必须用 closing 才能在 Windows 上释放文件句柄。
        with closing(sqlite3.connect(self.sqlite_path)) as connection:
            connection.execute(
                """
                CREATE TABLE RcPocReportTemplate (
                    Id INTEGER NOT NULL PRIMARY KEY,
                    ReportCode TEXT NOT NULL,
                    SnapshotJson TEXT NOT NULL,
                    BindingJson TEXT NOT NULL,
                    Revision INTEGER NOT NULL,
                    CreateTime TEXT NULL,
                    UpdateTime TEXT NULL,
                    CreateUserId INTEGER NULL,
                    CreateUserName TEXT NULL,
                    UpdateUserId INTEGER NULL,
                    UpdateUserName TEXT NULL
                )
                """
            )
            for offset, report_code in enumerate(report_codes):
                connection.execute(
                    """
                    INSERT INTO RcPocReportTemplate (
                        Id, ReportCode, SnapshotJson, BindingJson, Revision,
                        CreateTime, UpdateTime, CreateUserId, CreateUserName,
                        UpdateUserId, UpdateUserName
                    ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
                    """,
                    (
                        1000 + offset,
                        report_code,
                        '{"id":"workbook"}',
                        '{"version":1}',
                        8,
                        "2026-07-15 08:40:26.0745577",
                        "2026-07-15 16:23:57.7469933",
                        None,
                        None,
                        None,
                        None,
                    ),
                )
            connection.commit()

    def test_load_source_template_requires_exactly_one_row(self) -> None:
        self._create_source(["dept-workload"])

        row = load_source_template(self.sqlite_path, "dept-workload")

        self.assertEqual("dept-workload", row.report_code)
        self.assertEqual(8, row.revision)
        self.assertEqual('{"id":"workbook"}', row.snapshot_json)
        self.assertEqual('{"version":1}', row.binding_json)
        self.assertEqual(1000, row.id)

    def test_load_source_template_rejects_a_missing_row(self) -> None:
        self._create_source([])

        with self.assertRaisesRegex(MigrationError, "恰好一条"):
            load_source_template(self.sqlite_path, "dept-workload")

    def test_load_source_template_rejects_duplicate_rows(self) -> None:
        self._create_source(["dept-workload", "dept-workload"])

        with self.assertRaisesRegex(MigrationError, "恰好一条"):
            load_source_template(self.sqlite_path, "dept-workload")

    def test_validate_target_database_only_accepts_report_center(self) -> None:
        validate_target_database("hd_report_center")

        with self.assertRaisesRegex(MigrationError, "必须是 hd_report_center"):
            validate_target_database("lis_admin")

    def test_create_source_backup_preserves_the_exact_database_bytes(self) -> None:
        self._create_source(["dept-workload"])
        source_hash = hashlib.sha256(self.sqlite_path.read_bytes()).hexdigest()

        backup_path = create_source_backup(self.sqlite_path, self.backup_directory)

        self.assertTrue(backup_path.is_file())
        self.assertEqual(source_hash, hashlib.sha256(backup_path.read_bytes()).hexdigest())
        self.assertNotEqual(self.sqlite_path, backup_path)

    def test_target_duplicate_guard_uses_a_parameterized_report_code(self) -> None:
        cursor = _ScalarCursor(1)

        with self.assertRaisesRegex(MigrationError, "已存在"):
            ensure_target_has_no_template(cursor, "dept-workload")

        self.assertIn("ReportCode = %s", cursor.command)
        self.assertEqual(("dept-workload",), cursor.parameters)


if __name__ == "__main__":
    unittest.main()
