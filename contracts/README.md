# Ndeipi Posts contract

`NdeipiPosts` is the ERC-721 contract feed posts are minted on, on Polygon next to NdeipiCoin
(NDEIPI, [`0xd8d4…ab3b`](https://polygonscan.com/token/0xd8d401df77080baa60718fa0b1f775eea272ab3b)).
NdeipiCoin itself is an ERC-20: it can hold balances, but not one token per post.

- **Minting:** `mint(to, tokenId, uri)`, callable only by a wallet with `MINTER_ROLE`, which is
  Ndeipi Enterprise Server's minting wallet. The API picks `tokenId` (the post id as a 128-bit
  number) and `uri` (`https://chat.ndeipi.com/nft/posts/{postId}`), and the queue hands both over.
  See [docs/ndeipi-queue.md](../docs/ndeipi-queue.md#minting-posts).
- **No duplicates:** minting an id that exists reverts, so a retried mint can't make a second token.
  `exists(tokenId)` lets a worker check before retrying.
- **Metadata:** `setTokenURI` lets the admin repoint a token if the site ever moves. It emits
  ERC-4906 `MetadataUpdate`, so marketplaces refresh.
- **Standard parts:** OpenZeppelin 5 `ERC721URIStorage` and `AccessControl`, compiled with
  Solidity 0.8.28 for Cancun.

## Build and test

```bash
npm install
npm test
```

The tests run against Hardhat's local chain.

## Deploy

1. **Choose the wallets.**
   - `ADMIN` can grant and revoke roles and repoint metadata. Use a multisig or a hardware wallet,
     not a server key.
   - `MINTER` is the wallet Ndeipi Enterprise Server signs mints with. It needs a little POL for gas.
2. **Deploy to Amoy, Polygon's testnet, first,** and run a post through the whole flow:
   ```bash
   ADMIN=0x… MINTER=0x… DEPLOYER_PRIVATE_KEY=0x… npm run deploy:amoy
   ```
3. **Then deploy to Polygon:**
   ```bash
   ADMIN=0x… MINTER=0x… DEPLOYER_PRIVATE_KEY=0x… POLYGON_RPC_URL=https://… npm run deploy:polygon
   ```
   To publish the source on Polygonscan, also run
   `npx hardhat verify --network polygon <address> <ADMIN> <MINTER>`, with `POLYGONSCAN_API_KEY` set.
4. **Point the API at it** in `appsettings.json` or `appsettings.Production.json`, then publish:
   ```json
   "Social": { "Nft": { "Chain": "polygon", "ContractAddress": "0x…" } }
   ```
   `Chain` must be the name Ndeipi Enterprise Server uses for Polygon. It's the same value it
   sees on transfers, e.g. NDEIPI's `polygon`.

Keys are only ever read from environment variables. Never put one in a file in this repo.
