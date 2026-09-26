require("@nomicfoundation/hardhat-toolbox");

// Deploying reads the deployer's key and an RPC URL from the environment, never from a file:
//   DEPLOYER_PRIVATE_KEY=0x…  POLYGON_RPC_URL=https://…  npm run deploy:polygon
const accounts = process.env.DEPLOYER_PRIVATE_KEY ? [process.env.DEPLOYER_PRIVATE_KEY] : [];

module.exports = {
  solidity: {
    version: "0.8.28",
    // Cancun: OpenZeppelin 5.x uses its MCOPY instruction. Polygon PoS has run Cancun since the
    // Napoli upgrade (March 2024).
    settings: { evmVersion: "cancun", optimizer: { enabled: true, runs: 200 } }
  },
  networks: {
    // Polygon's testnet: try the whole flow here first.
    amoy: { url: process.env.AMOY_RPC_URL || "https://rpc-amoy.polygon.technology", chainId: 80002, accounts },
    polygon: { url: process.env.POLYGON_RPC_URL || "https://polygon-rpc.com", chainId: 137, accounts }
  },
  etherscan: { apiKey: process.env.POLYGONSCAN_API_KEY || "" }
};
