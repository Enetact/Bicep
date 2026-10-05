# Databricks notebook source
# Workspace-relative helper is uploaded with this notebook by the bundle.
import json
from contracts import MAX_BYTES, VERSION, parse_batch, paths
from delta.tables import DeltaTable

for name in ("lake_account", "source_relative_path", "batch_id", "stage", "expected_sha256"):
    dbutils.widgets.text(name, "")
p = {name: dbutils.widgets.get(name) for name in
     ("lake_account", "source_relative_path", "batch_id", "stage", "expected_sha256")}
if p["stage"] not in ("validate", "curate", "verify"):
    raise ValueError("Unsupported reviewed stage")
source, destination = paths(p["lake_account"], p["source_relative_path"], p["batch_id"])
files = dbutils.fs.ls(source)
if len(files) != 1 or files[0].isDir() or files[0].size > MAX_BYTES:
    raise ValueError("Expected one file of at most 10 MiB")
selected = (spark.read.format("binaryFile").load(source)
            .where(f"length > 0 AND length <= {MAX_BYTES}").select("content").limit(2).collect())
if len(selected) != 1:
    raise ValueError("Source size changed or file is empty")
raw = bytes(selected[0][0])
rows, digest = parse_batch(raw)
if p["stage"] != "validate" and digest != p["expected_sha256"]:
    raise ValueError("Source changed after validation; batch stopped")

if p["stage"] == "validate":
    dbutils.jobs.taskValues.set(key="source_sha256", value=digest)
else:
    spark.conf.set("spark.sql.session.timeZone", "UTC")
    expected = spark.createDataFrame(
        [row + (p["batch_id"], digest, VERSION) for row in rows],
        "record_id string, event_time timestamp, amount decimal(18,4), "
        "_batch_id string, _source_sha256 string, _contract_version string")
    exists = DeltaTable.isDeltaTable(spark, destination)
    if p["stage"] == "curate" and not exists:
        # One atomic Delta commit; never overwrite existing batches or delete input.
        expected.write.format("delta").mode("errorifexists").save(destination)
    if not DeltaTable.isDeltaTable(spark, destination):
        raise ValueError("Expected committed Delta batch is absent")
    actual = spark.read.format("delta").load(destination)
    if actual.columns != expected.columns or actual.count() != len(rows):
        raise ValueError("Existing batch differs; manual reconciliation required")
    if expected.exceptAll(actual).limit(1).count() or actual.exceptAll(expected).limit(1).count():
        raise ValueError("Batch verification failed; existing data preserved")

# Metadata only. Raw records and credentials never appear in the result receipt.
dbutils.notebook.exit(json.dumps({"contract": VERSION, "stage": p["stage"],
    "batchId": p["batch_id"], "rows": len(rows), "sourceSha256": digest}))
