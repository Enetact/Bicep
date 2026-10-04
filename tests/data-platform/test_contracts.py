"""Offline domain tests; Spark, Delta commits and live identities need Azure acceptance."""
import importlib.util
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("contracts", ROOT / "workloads/private-data-platform/databricks/src/contracts.py")
c = importlib.util.module_from_spec(spec)
spec.loader.exec_module(c)
HEADER = b"record_id,event_time,amount\n"
GOOD = b"a,2026-01-01T00:00:00Z,12.5000\n"
UUID = "12345678-1234-1234-1234-123456789abc"


class Contracts(unittest.TestCase):
    def test_sample(self):
        rows, digest = c.parse_batch((ROOT / "workloads/private-data-platform/databricks/sample.csv").read_bytes())
        self.assertEqual(2, len(rows))
        self.assertEqual(64, len(digest))

    def test_duplicates_collapse(self):
        self.assertEqual(1, len(c.parse_batch(HEADER + GOOD + GOOD)[0]))

    def test_conflicting_duplicate_rejected(self):
        with self.assertRaises(ValueError): c.parse_batch(HEADER + GOOD + GOOD.replace(b"12.5000", b"9"))

    def test_empty_and_header_only_rejected(self):
        for value in [b"", HEADER]:
            with self.subTest(value=value), self.assertRaises(ValueError): c.parse_batch(value)

    def test_invalid_schema(self):
        for value in [b"other\n", HEADER + b"a,b\n", HEADER + GOOD.replace(b"Z,", b"Z,extra,")]:
            with self.subTest(value=value), self.assertRaises(ValueError): c.parse_batch(value)

    def test_invalid_values(self):
        for row in [b"x/../y,2026-01-01T00:00:00Z,1", b"a,2026-02-30T00:00:00Z,1", b"a,2026-01-01,1", b"a,2026-01-01T00:00:00Z,NaN", b"a,2026-01-01T00:00:00Z,0.00001"]:
            with self.subTest(row=row), self.assertRaises(ValueError): c.parse_batch(HEADER + row)

    def test_row_and_byte_bounds(self):
        for raw in [HEADER + GOOD * (c.MAX_ROWS + 1), b"x" * (c.MAX_BYTES + 1)]:
            with self.assertRaises(ValueError): c.parse_batch(raw)

    def test_content_change_changes_receipt(self):
        self.assertNotEqual(c.parse_batch(HEADER + GOOD)[1], c.parse_batch(HEADER + GOOD.replace(b"12.5000", b"1"))[1])

    def test_paths_are_owned_and_distinct(self):
        source, target = c.paths("stexample", f"staged/{UUID}/batch.csv", UUID)
        self.assertIn("landing@stexample", source)
        self.assertIn("curated@stexample", target)
        self.assertTrue(target.endswith(UUID))
        c.paths("stexample", "inbox/sample.csv", "123")

    def test_unsafe_or_cross_batch_paths(self):
        for relative in ["../sample.csv", "inbox/../sample.csv", "https://other", "inbox/a*.csv", "inbox/%2f.csv", f"staged/{UUID}/batch.csv"]:
            with self.subTest(relative=relative), self.assertRaises(ValueError): c.paths("stexample", relative, "123")
        for account, batch in [("bad.example", "123"), ("valid", "../x"), ("valid", "0")]:
            with self.assertRaises(ValueError): c.paths(account, "inbox/sample.csv", batch)


if __name__ == "__main__":
    unittest.main()
