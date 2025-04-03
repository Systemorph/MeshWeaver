using './db-migrations.module.bicep'

param azure_postgres_outputs_connectionstring = '{{ .Env.AZURE_POSTGRES_CONNECTIONSTRING }}'
param db_migrations_containerimage = '{{ .Image }}'
param db_migrations_containerport = '{{ targetPortOrDefault 8080 }}'
param outputs_azure_container_apps_environment_id = '{{ .Env.AZURE_CONTAINER_APPS_ENVIRONMENT_ID }}'
param outputs_azure_container_registry_endpoint = '{{ .Env.AZURE_CONTAINER_REGISTRY_ENDPOINT }}'
param outputs_azure_container_registry_managed_identity_id = '{{ .Env.AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_ID }}'
param outputs_managed_identity_client_id = '{{ .Env.MANAGED_IDENTITY_CLIENT_ID }}'
