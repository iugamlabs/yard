# Modules

Group endpoints, contracts, operations, entities and EF mappings by business capability. Endpoints delegate persistence to operations. Use one production project and one database; coordinate cross-module writes through the owning module's operations. Notes is a removable example, not a framework requirement. Remove its page/client wrappers and create a migration when removing its model.
