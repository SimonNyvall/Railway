# Railway

Work in progress project.
The plan is to have local pipelines that are connected to a repo.

## Ideas

- Local runtime, uses the users own env to execute.
- Docker runtime, to be able to start up a new process without locking the repo.
- Yaml files for pipelines.
- Import github, gitlab and azure devops pipelines and have them ported.
- git commit trigger.
- Trigger the pipeline manually.
- MCP server to let local ai excute the pipelines.

## MVP

- Be able to link pipeline to a repo
- Have the user manually trigger the pipeline
- List pipelines
- Validate the pipeline (should be able to pass in the pipeline yaml and get validation back)