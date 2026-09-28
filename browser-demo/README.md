# Browser demo (no installation)

`khata-demo.html` runs the project's real PostgreSQL database inside the browser
using [PGlite](https://pglite.dev) (PostgreSQL compiled to WebAssembly). It loads
`../database/accounting_demo.sql` and offers a dashboard, new sales (same
calculation order and rounding as the Django backend), receipts with FIFO
allocation, stock register, as-of receivables aging, customer ledger and a SQL
console. Changes live only in the open tab.

To serve it yourself, place these files next to the page and open it over HTTP:

```bash
npm pack @electric-sql/pglite@0.5.8 && tar xzf electric-sql-pglite-0.5.8.tgz
cp package/dist/pglite.wasm package/dist/initdb.wasm .
gzip -9 -c package/dist/pglite.data | base64 -w0 > pglite-data.gz.b64.txt
cp ../database/accounting_demo.sql .
python3 -m http.server 8000   # then open http://localhost:8000/khata-demo.html
```

It is a demonstration of the data model and business rules; the full
application (authentication, roles, PDFs, exports, audit log) is the Django +
SvelteKit stack in this repository.
