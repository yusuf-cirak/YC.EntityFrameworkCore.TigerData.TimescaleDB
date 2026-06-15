# Changelog

## [1.0.1](https://github.com/yusuf-cirak/YC.EntityFrameworkCore.TigerData.TimescaleDB/compare/v1.0.0...v1.0.1) (2026-06-15)


### Miscellaneous Chores

* release 1.0.1 ([9cb45bd](https://github.com/yusuf-cirak/YC.EntityFrameworkCore.TigerData.TimescaleDB/commit/9cb45bd2945d9f818433917cf7574cdfeaf8c17e))

## [1.0.0](https://github.com/yusuf-cirak/YC.EntityFrameworkCore.TigerData.TimescaleDB/compare/v0.1.0...v1.0.0) (2026-06-14)


### ⚠ BREAKING CHANGES

* **api:** [Hypertable], [HypertablePartition], [ColumnstorePolicy], [ReorderPolicy] and [RetentionPolicy] are removed. Use [PartitionColumn], [Columnstore(CompressAfter, ...)], [Retention(after, Every)], [SegmentBy]/[OrderBy(Sort, Nulls)]. Fluent string interval overloads are gone; use TimeSpan or (int, Every), and the long overloads for integer-time hypertables.

### Features

* **migrations:** add schema features and data-op toggles, harden coverage ([319f688](https://github.com/yusuf-cirak/YC.EntityFrameworkCore.TigerData.TimescaleDB/commit/319f688ba801f0ea64071c2acc143e248ea8c4b2))
* TimescaleDB provider extension for EF Core 10 ([f62216d](https://github.com/yusuf-cirak/YC.EntityFrameworkCore.TigerData.TimescaleDB/commit/f62216d8c99eb0a35c99f98de38d7cb0cfda5912))


### Code Refactoring

* **api:** magic-string-free, dependency-safe attribute surface ([640ab92](https://github.com/yusuf-cirak/YC.EntityFrameworkCore.TigerData.TimescaleDB/commit/640ab920d262f23c1c105ba131a9f6b133394cc6))
