# Yard-generated application

Read yard.json and README.md first. Use yard init, yard dev and yard check. Use yard add to create applications and yard sync for managed engineering configuration. Do not add Taskfile or Procfile.

Each backend has one production project, organized by Modules, Infrastructure and Hosting. Group endpoints, contracts, operations, entities and EF mappings by capability. Coordinate cross-module writes through owning operations. Notes is removable example code, not a required platform module.

Production source uses standard ASP.NET Core and EF Core, never Yard packages, base classes or management branches. AddApplication/UseApplication compose services and routes without migrations, seeds or explicit service startup. Database configuration is shared with the standard design-time factory; use ConnectionStrings:Default. Do not read .yard or yard.json from application code.

Use yard db generate and yard db migrate for the selected provider. API changes require yard contracts. Use yard fmt for the complete C# and frontend formatting pipelines. Test databases and generated fixtures belong in .temp; never reuse development or production data. Keep .yard, .temp and secrets untracked. sync must not overwrite business source or migrations.
