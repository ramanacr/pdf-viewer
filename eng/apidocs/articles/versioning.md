# Versioning

The engine packages have their own version, separate from the PDF Viewer application.

- A tag `engine-vX.Y.Z` builds and publishes version `X.Y.Z` of all three packages (for example
  `engine-v0.1.0` gives `0.1.0`). Pre-release versions such as `0.1.0-preview.1` are allowed.
- Builds of `main` and of pull requests make `0.0.0-ci.<run number>`. These stay a build artifact.
- A local `dotnet pack` makes `0.0.0-dev`.
- The application's `v*` tags do not change the package version.

The three packages always have the same version. Reference the same version of each.

The API is not stable before version 1.0: a minor version can change it. Read the
[commit history](https://github.com/ramanacr/pdf-viewer/commits/main/src) before you update.

The workflow is `.github/workflows/packages.yml`. It publishes with nuget.org Trusted Publishing
(no stored API key). Each package has a symbol package (`.snupkg`), and SourceLink maps the
symbols to the exact source on GitHub.
