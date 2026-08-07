# Unity Network Example

Unity consumer project for `network-example`.

## Package Dependency

This project consumes:

```text
network-example/plugins/com.network-example.kernel
```

through Unity Package Manager using:

```json
"com.network-example.kernel": "file:/Users/kasaki/Projects/network-example/plugins/com.network-example.kernel"
```

## Importing Skeleton Assets

Kernel-driven skeletons have import rules that are not obvious and fail
silently. See [docs/skeleton-asset-import.md](docs/skeleton-asset-import.md)
before adding one.

## Expected Workspace Layout

```text
Projects/
├── network-example/
└── unity-network-example/
```
