"""Bounded starter data contract. Pure Python; no Azure, Spark or credentials."""
import csv
import hashlib
import io
import re
from datetime import datetime, timezone
from decimal import Decimal

MAX_BYTES = 10 * 1024 * 1024
MAX_ROWS = 10_000
VERSION = "platform-csv-v1"


def paths(account, relative, batch):
    if not re.fullmatch(r"[a-z0-9]{3,24}", account):
        raise ValueError("Invalid approved lake account")
    if not re.fullmatch(r"(?:[0-9a-f]{8}-(?:[0-9a-f]{4}-){3}[0-9a-f]{12}|[1-9][0-9]{0,19})", batch):
        raise ValueError("Batch ID must be an ADF UUID or Databricks run ID")
    if not re.fullmatch(r"(?:inbox/[a-zA-Z0-9][a-zA-Z0-9_-]{0,63}\.csv|staged/[0-9a-f]{8}-(?:[0-9a-f]{4}-){3}[0-9a-f]{12}/batch\.csv)", relative):
        raise ValueError("Source must be a reviewed inbox CSV or isolated staged batch")
    if relative.startswith("staged/") and relative.split("/")[1] != batch:
        raise ValueError("Staged source does not belong to this batch")
    return (f"abfss://landing@{account}.dfs.core.windows.net/{relative}",
            f"abfss://curated@{account}.dfs.core.windows.net/batches/{batch}")


def parse_batch(raw):
    if not raw or len(raw) > MAX_BYTES:
        raise ValueError("Input must contain at most 10 MiB")
    reader = csv.reader(io.StringIO(raw.decode("utf-8-sig")), strict=True)
    if next(reader, None) != ["record_id", "event_time", "amount"]:
        raise ValueError("Expected exact record_id,event_time,amount header")
    rows = {}
    count = 0
    for values in reader:
        count += 1
        if count > MAX_ROWS or len(values) != 3:
            raise ValueError("Invalid row width or more than 10000 rows")
        key, timestamp, amount = values
        if not re.fullmatch(r"[a-zA-Z0-9_-]{1,64}", key):
            raise ValueError("Invalid record ID")
        if not re.fullmatch(r"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z", timestamp):
            raise ValueError("Timestamp must be UTC ISO seconds")
        instant = datetime.strptime(timestamp, "%Y-%m-%dT%H:%M:%SZ").replace(tzinfo=timezone.utc)
        if not re.fullmatch(r"-?\d{1,14}(?:\.\d{1,4})?", amount):
            raise ValueError("Amount must fit decimal(18,4)")
        row = (key, instant, Decimal(amount))
        if key in rows and rows[key] != row:
            raise ValueError("Conflicting records for the same ID; nothing written")
        rows[key] = row
    if not rows:
        raise ValueError("Empty batch; nothing written")
    return [rows[key] for key in sorted(rows)], hashlib.sha256(raw).hexdigest()
