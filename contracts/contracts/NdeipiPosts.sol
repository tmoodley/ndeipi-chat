// SPDX-License-Identifier: MIT
pragma solidity ^0.8.24;

import {ERC721} from "@openzeppelin/contracts/token/ERC721/ERC721.sol";
import {ERC721URIStorage} from "@openzeppelin/contracts/token/ERC721/extensions/ERC721URIStorage.sol";
import {AccessControl} from "@openzeppelin/contracts/access/AccessControl.sol";

/// @title Ndeipi Posts
/// @notice One token per Ndeipi feed post, minted to its author by Ndeipi Enterprise Server.
/// @dev The API picks each token id (the post id as a 128-bit number) and the metadata URL
///      (https://chat.ndeipi.com/nft/posts/{postId}); the queue hands both to the minter. See
///      docs/ndeipi-queue.md, "Minting posts".
contract NdeipiPosts is ERC721URIStorage, AccessControl {
    /// @notice Held by Ndeipi Enterprise Server's minting wallet.
    bytes32 public constant MINTER_ROLE = keccak256("MINTER_ROLE");

    constructor(address admin, address minter) ERC721("Ndeipi Posts", "NDPOST") {
        _grantRole(DEFAULT_ADMIN_ROLE, admin);
        _grantRole(MINTER_ROLE, minter);
    }

    /// @notice Mints a post to its author. Reverts if the token already exists, so a retried
    ///         mint can never create a second token for the same post.
    function mint(address to, uint256 tokenId, string calldata uri) external onlyRole(MINTER_ROLE) {
        _safeMint(to, tokenId);
        _setTokenURI(tokenId, uri);
    }

    /// @notice Whether a post has been minted: the worker's check before retrying a mint whose
    ///         outcome it didn't record.
    function exists(uint256 tokenId) external view returns (bool) {
        return _ownerOf(tokenId) != address(0);
    }

    /// @notice Points a token at new metadata, e.g. if the site moves. Emits ERC-4906's
    ///         MetadataUpdate, so marketplaces refresh it.
    function setTokenURI(uint256 tokenId, string calldata uri) external onlyRole(DEFAULT_ADMIN_ROLE) {
        _requireOwned(tokenId);
        _setTokenURI(tokenId, uri);
    }

    function supportsInterface(bytes4 interfaceId) public view override(ERC721URIStorage, AccessControl) returns (bool) {
        return super.supportsInterface(interfaceId);
    }
}
