using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ServiceScheduler.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "customers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    phone = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_customers", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "dealerships",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    address = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dealerships", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "service_types",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    duration_minutes = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_service_types", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "vehicles",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    vin = table.Column<string>(type: "character varying(17)", maxLength: 17, nullable: false),
                    make = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    model = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    year = table.Column<int>(type: "integer", nullable: false),
                    fuel_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_vehicles", x => x.id);
                    table.ForeignKey(
                        name: "FK_vehicles_customers_customer_id",
                        column: x => x.customer_id,
                        principalTable: "customers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "service_bays",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    dealership_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_service_bays", x => x.id);
                    table.ForeignKey(
                        name: "FK_service_bays_dealerships_dealership_id",
                        column: x => x.dealership_id,
                        principalTable: "dealerships",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "technicians",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    dealership_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_technicians", x => x.id);
                    table.ForeignKey(
                        name: "FK_technicians_dealerships_dealership_id",
                        column: x => x.dealership_id,
                        principalTable: "dealerships",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "appointments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    dealership_id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    vehicle_id = table.Column<Guid>(type: "uuid", nullable: false),
                    service_type_id = table.Column<Guid>(type: "uuid", nullable: false),
                    technician_id = table.Column<Guid>(type: "uuid", nullable: false),
                    service_bay_id = table.Column<Guid>(type: "uuid", nullable: false),
                    start_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    end_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_appointments", x => x.id);
                    table.ForeignKey(
                        name: "FK_appointments_customers_customer_id",
                        column: x => x.customer_id,
                        principalTable: "customers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_appointments_dealerships_dealership_id",
                        column: x => x.dealership_id,
                        principalTable: "dealerships",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_appointments_service_bays_service_bay_id",
                        column: x => x.service_bay_id,
                        principalTable: "service_bays",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_appointments_service_types_service_type_id",
                        column: x => x.service_type_id,
                        principalTable: "service_types",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_appointments_technicians_technician_id",
                        column: x => x.technician_id,
                        principalTable: "technicians",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_appointments_vehicles_vehicle_id",
                        column: x => x.vehicle_id,
                        principalTable: "vehicles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_appointments_customer_id",
                table: "appointments",
                column: "customer_id");

            migrationBuilder.CreateIndex(
                name: "IX_appointments_dealership_id_start_at",
                table: "appointments",
                columns: new[] { "dealership_id", "start_at" });

            migrationBuilder.CreateIndex(
                name: "IX_appointments_service_bay_id",
                table: "appointments",
                column: "service_bay_id");

            migrationBuilder.CreateIndex(
                name: "IX_appointments_service_type_id",
                table: "appointments",
                column: "service_type_id");

            migrationBuilder.CreateIndex(
                name: "IX_appointments_technician_id",
                table: "appointments",
                column: "technician_id");

            migrationBuilder.CreateIndex(
                name: "IX_appointments_vehicle_id",
                table: "appointments",
                column: "vehicle_id");

            migrationBuilder.CreateIndex(
                name: "IX_service_bays_dealership_id",
                table: "service_bays",
                column: "dealership_id");

            migrationBuilder.CreateIndex(
                name: "IX_technicians_dealership_id",
                table: "technicians",
                column: "dealership_id");

            migrationBuilder.CreateIndex(
                name: "IX_vehicles_customer_id",
                table: "vehicles",
                column: "customer_id");

            migrationBuilder.CreateIndex(
                name: "IX_vehicles_vin",
                table: "vehicles",
                column: "vin",
                unique: true);

            // The booking invariant. EF Core cannot express an EXCLUDE constraint, so it is
            // written by hand. A resource is constrained independently of the other: one
            // combined constraint would only reject rows matching on BOTH bay and technician,
            // which would let the same technician be booked twice in different bays.
            //
            //   '[)'          half-open, so an appointment ending at 10:00 does not collide
            //                 with one starting at 10:00.
            //   WHERE status  partial, so a cancelled appointment stops blocking its slot.
            //   btree_gist    lets a uuid equality share a GiST index with a range overlap.
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS btree_gist;");

            migrationBuilder.Sql("""
                ALTER TABLE appointments
                  ADD CONSTRAINT appointments_no_overlap_technician
                  EXCLUDE USING gist (
                    technician_id WITH =,
                    tstzrange(start_at, end_at, '[)') WITH &&
                  ) WHERE (status = 1);
                """);

            migrationBuilder.Sql("""
                ALTER TABLE appointments
                  ADD CONSTRAINT appointments_no_overlap_service_bay
                  EXCLUDE USING gist (
                    service_bay_id WITH =,
                    tstzrange(start_at, end_at, '[)') WITH &&
                  ) WHERE (status = 1);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "ALTER TABLE appointments DROP CONSTRAINT IF EXISTS appointments_no_overlap_service_bay;");
            migrationBuilder.Sql(
                "ALTER TABLE appointments DROP CONSTRAINT IF EXISTS appointments_no_overlap_technician;");

            migrationBuilder.DropTable(
                name: "appointments");

            migrationBuilder.DropTable(
                name: "service_bays");

            migrationBuilder.DropTable(
                name: "service_types");

            migrationBuilder.DropTable(
                name: "technicians");

            migrationBuilder.DropTable(
                name: "vehicles");

            migrationBuilder.DropTable(
                name: "dealerships");

            migrationBuilder.DropTable(
                name: "customers");
        }
    }
}
