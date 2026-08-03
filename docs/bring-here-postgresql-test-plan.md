# Bring Here PostgreSQL qualification

The persistence slice is qualified only when real PostgreSQL tests prove:

1. installation ownership cannot be reassigned;
2. a location can be created only for its registered owner;
3. divergent updates require the exact previously observed state/environment pair;
4. timestamps do not override a divergent head;
5. exact removal preserves a changed claim;
6. concurrent first publication produces one `Created` and one `Conflict`, never a raw uniqueness/serialization failure;
7. owner-scoped listing returns only the authenticated owner's claims.

The API, Steam authentication binding and immutable payload transfer remain separate stacked slices.
