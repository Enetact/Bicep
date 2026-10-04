# Databricks notebook source
# Import manually at the reviewed ADF notebook path. No data writes or secrets.
# This tests job execution only; Unity Catalog and ADLS acceptance are separate.
result = spark.sql("SELECT 1 AS platform_acceptance").collect()[0][0]
assert result == 1, "Unexpected execution result"
dbutils.notebook.exit("platform-execution-accepted; data-access-not-tested")
