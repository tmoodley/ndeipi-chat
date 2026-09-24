# Livestock registry

This implements the functional addendum *Ndeipi Super App Livestock Registry Service*. A farmer
registers a cow by photographing its face and its side. A later photo of the same animal records
a health check.

## How it works

```
 App (Herd tab)                                  API  POST /api/v1/livestock/register
 ─────────────────                               ──────────────────────────────────────────────
 1. face photo  (guide frame: muzzle)            1. verify the device signature          → 403
 2. side photo  (guide frame: whole body)        2. same upload again? return the answer  → 200
 3. ranch, breed, sex, age, wallet               3. check metadata                        → 400
 4. saved to the SQLite queue on the phone       4. check the photos: JPEG/PNG, ≥1080 px   → 422
 5. signed with the phone's key and uploaded     5. Claude: photo quality, breed, traits,
    now, or when the signal returns                 body condition, visible health signs   → 422 if unusable
                                                 6. muzzle-print model: identity check    → 409 if another owner's
                                                 7. store LivestockMaster + LivestockHealthAudit
                                                 8. respond; the app shows the result
```

## Mapping to the spec

**Endpoint.** It follows the spec: `POST /api/v1/livestock/register` takes
`multipart/form-data` with `face_image`, `flank_image` and `metadata`. `metadata` can be sent as a
text field or as a JSON file part. The caller must also be signed in with Clerk, like every other
endpoint in the app.

**Signature.** The spec's `X-Ndeipi-Signature` holds an ECDSA signature. You chose device keys
over Ethereum wallets, so the details are:

- On first use, the app creates a **P-256** key in the phone's secure storage. It registers the
  public half with `POST /api/v1/livestock/operator-keys` and gets back a key id.
- Every upload sends that id in an extra header, `X-Ndeipi-Key-Id`.
- The signature is over the bytes of `LivestockContract.SigningPayload`: the line
  `ndeipi-livestock-register-v1`, then the SHA-256 hex of the face photo, the side photo and the
  metadata JSON, one per line.
- The signature is SHA-256, IEEE P1363 (r‖s), base64-encoded.
- A key belongs to one user. Another user's key, or photos changed after signing, get **403**.

**Metadata.** It follows the spec, with one addition. `existingCowId` names the animal for a
routine health check. If a muzzle model is running, the photos must match that animal.

**Response.** It uses the spec's shape, with these additions:

| Field | What it means |
| --- | --- |
| `biometrics.enrollmentStatus` | `NEW_REGISTRATION`, `EXISTING_ANIMAL` (the muzzle matched one of your animals, so this was recorded as a health check) or `UNVERIFIED_IDENTITY` (no muzzle model is configured) |
| `biometrics.similarityThreshold` | The configured threshold. |
| `biometrics.closestMatchSimilarity` | The best score found against the register. |
| `phenotype.breedConfirmed` / `recordedBreed` | The spec requires ≥ 90% confidence. Below that, the farmer's claimed breed is recorded (`BreedSource = CLAIMED`) and the model's guess is still reported. |
| `phenotype.morphologicalTraits.earShape` / `coatPattern` | Extra traits, recorded as free text. |
| `problems` | On a refusal, what to fix, written for the farmer. |
| `attestationHash` | `0x` + SHA-256 over the record, the photo hashes and the operator's signature. |

**Status codes.**

| Code | `status` | When |
| --- | --- | --- |
| 200 | `SUCCESS` | Registered, or recorded as a health check. A resent upload returns the original answer. |
| 400 | `REJECTED` | The metadata breaks the contract. |
| 403 | `REJECTED` | The signature isn't from one of your registered device keys. |
| 409 | `DUPLICATE` | The muzzle matches an animal registered to another owner. |
| 422 | `REJECTED` | The photos are too small, unreadable, not cattle, or unusable. The response says why. |
| 502/503 | `UNAVAILABLE` | Analysis isn't configured, or Claude is unreachable. The app keeps the capture and retries. |

**Schema.** `LivestockMaster` and `LivestockHealthAudit` use the spec's names and columns, with
these differences:

- `OwnerWallet` is nullable, so farmers without a `0x` wallet can still register.
- `AttestationHash` is stored without its `0x` prefix. The spec's column is 64 characters, but `0x` plus 64 hex digits is 66.
- Added to `LivestockMaster`:
  - `OwnerUserId`
  - `MuzzleEmbedding` and `EmbeddingModel`
  - `BreedSource`
  - `ApproximateAgeMonths`
  - `GpsAccuracyMeters`
  - `FaceImageRef`
  - `MorphologyJson`
- Added to `LivestockHealthAudit`:
  - `HydrationStatus`
  - `FaceImageRef`
  - the operator, key and signature
  - `SubmissionHash`, which makes resends idempotent
  - `AssessmentModel`
  - the stored response
- `BiometricMuzzleHash` is the SHA-256 of the muzzle embedding. Without a model, it's the SHA-256 of the face photo instead.

**Not implemented.**

- **Gait and stance screening in video mode.** Lameness is only flagged when it's visible in the still side photo.
- **Minting the animal as a token.** The attestation hash is ready to anchor on-chain, but nothing queues it to Ndeipi yet.

## Photo analysis (Claude)

`ClaudeCattleAssessor` sends both photos to Claude, downscaled to 1568 px on the long edge, and
forces a structured answer through a tool. Claude reports:

- whether each photo is usable, and what to change if not
- whether both photos show the same head of cattle
- the breed, from the spec's taxonomy plus `Crossbreed` and `Other`, with a confidence
- morphology: hump, dewlap, horns, ears and coat
- body condition on the 9-point scale
- visible signs: ticks, lumpy skin nodules, ringworm, wounds, eye or nasal discharge, corneal opacity, sunken eyes and lameness posture
- hydration
- a bounding box for the muzzle

Registry rules are applied on top of the model's answer:

- **Vet inspection is required** when any of these hold:
  - lumpy skin nodules
  - corneal opacity
  - sunken eyes
  - wounds
  - discharge
  - lameness at 50% confidence or more
  - severe dehydration
  - body condition below 3 or above 8
- **An animal that needs a vet is never rated better than `FAIR`.**

Treat these as screening, not diagnosis. A model's self-reported confidence isn't calibrated, and
body condition from a single photo is an estimate. Before relying on the numbers, validate them on
a sample of animals your vets have scored.

## Muzzle-print identity (your ONNX model)

Duplicate detection needs a model that turns a muzzle image into an embedding. That is the
spec's 512-dimensional vector, and it has to come from you. Set `Livestock:Muzzle:ModelPath` to
an ONNX file that:

- takes **one** input: float32, NCHW `[1, 3, H, W]`, RGB, with each channel normalised as
  `(pixel/255 − mean) / std`. The defaults are 224×224 with ImageNet mean and std; override them with
  `InputWidth`, `InputHeight`, `Mean` and `Std`.
- returns **one** output: the embedding, of any length. The API L2-normalises it and compares by cosine similarity.

Each face photo is cropped to the muzzle box Claude finds, with 10% padding, before it goes to
the model.

- **Threshold.** Set `Livestock:Muzzle:SimilarityThreshold` (default 0.90) from your model's
  genuine-versus-impostor score distributions.
- **Changing models.** Set `ModelId`. Embeddings from different models aren't compared, so animals
  enrolled under the old model need re-scanning.
- **Scale.** Enrolled embeddings are held in memory and synced incrementally. For very large herds,
  move them to a vector index such as SQL Server 2025's `VECTOR`.

Without a model, registration still works and the response says `UNVERIFIED_IDENTITY`, but the
registry can't catch an animal registered twice.

The tests use a real but tiny ONNX model, written by hand in `tests/.../LivestockFakes.cs`: grid
average-pooling. This exercises ONNX Runtime, the preprocessing and the threshold without any
downloaded weights.

## Configuration

| Key | Default | |
| --- | --- | --- |
| `Livestock:Claude:ApiKey` | — | Required. Without it, registration answers 503. |
| `Livestock:Claude:Model` | `claude-opus-5` | |
| `Livestock:Muzzle:ModelPath` | — | The ONNX muzzle-print model. See above. |
| `Livestock:Muzzle:SimilarityThreshold` | `0.9` | |
| `Livestock:MinFaceShortSide` / `MinFlankShortSide` | `1080` / `720` | Pixels on the shorter side. |
| `Livestock:BreedConfidenceThreshold` | `0.9` | |
| `Livestock:MaxCaptureAge` | 30 days | How long a capture can wait on a phone before it's refused. |
| `Livestock:ImageStoragePath` | `App_Data/livestock-images` | Content-addressed local storage. Use blob storage when running more than one API instance. |

Photos are private. `GET /api/v1/livestock/images/{ref}` serves them only to the animal's owner.
Add `?width=` to get a downscaled JPEG for lists.

## In the app

The Herd tab lists your animals, with a vet flag where one is needed, plus any captures still
waiting on the phone.

**Registering.** Register a cow → face photo → side photo → details → Register. The camera shows
a guide frame: a near-square for the muzzle and forehead, and a wide one for the whole body. It
captures at about 12 MP, and photos below the registry's minimum size are refused before the
farmer leaves the animal. If the camera preview isn't available, the phone's camera app or the
gallery can be used instead.

**Offline.** Every capture is saved to SQLite first, then uploaded. With no signal it waits and
goes up on launch or when the connection returns. A capture the server refuses stays on the phone
with the reason, until the farmer retakes or discards it.

A cow's record shows every health check, and **New health check** re-screens that animal.
