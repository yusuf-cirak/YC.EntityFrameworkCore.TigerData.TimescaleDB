using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TimescaleDb.Sample.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS timescaledb;");

            migrationBuilder.CreateTable(
                name: "readings",
                columns: table => new
                {
                    time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    device_id = table.Column<string>(type: "text", nullable: false),
                    value = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                })
                .Annotation("TimescaleDb:Columnstore:Enabled", true)
                .Annotation("TimescaleDb:Columnstore:OrderBy", "time DESC")
                .Annotation("TimescaleDb:Columnstore:SegmentBy", "device_id")
                .Annotation("TimescaleDb:ColumnstorePolicy:After", "7 days")
                .Annotation("TimescaleDb:Hypertable:ChunkInterval", "1 day")
                .Annotation("TimescaleDb:Hypertable:PartitionColumn", "time")
                .Annotation("TimescaleDb:IsHypertable", true)
                .Annotation("TimescaleDb:RetentionPolicy:DropAfter", "90 days");

            migrationBuilder.Sql("CREATE MATERIALIZED VIEW \"hourly_averages\"\r\nWITH (timescaledb.continuous) AS\r\nSELECT time_bucket(INTERVAL '1 hour', time) AS bucket,\n       device_id,\n       avg(value) AS average\nFROM readings\nGROUP BY 1, 2\r\nWITH NO DATA;");

            migrationBuilder.Sql("SELECT add_continuous_aggregate_policy('\"hourly_averages\"', start_offset => INTERVAL '3 days', end_offset => INTERVAL '01:00:00', schedule_interval => INTERVAL '01:00:00');");

            migrationBuilder.Sql("SELECT add_job('public.sample_noop'::regproc, schedule_interval => INTERVAL '1 day', job_name => 'sample_noop_job');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP MATERIALIZED VIEW IF EXISTS \"hourly_averages\";");

            migrationBuilder.DropTable(
                name: "readings");

            migrationBuilder.Sql("SELECT delete_job(job_id) FROM timescaledb_information.jobs WHERE application_name = 'sample_noop_job' OR application_name LIKE 'sample_noop_job [%]';");
        }
    }
}
