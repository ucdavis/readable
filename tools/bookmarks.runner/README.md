# bookmarks.runner

Small console runner to preview what `PdfBookmarkService` would add to a tagged PDF.

## Run

From the repo root:

```bash
dotnet run --project tools/bookmarks.runner -- --input /path/to/input.pdf
```

Write a specific output file:

```bash
dotnet run --project tools/bookmarks.runner -- --input /path/to/input.pdf --output /path/to/output.pdf
```

The tool prints the outline tree (title + destination page) and writes a PDF you can open to visually confirm the bookmarks.

