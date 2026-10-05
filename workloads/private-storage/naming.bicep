@export()
@sealed()
type ResourceNames = {
  profile: 'standard-v2'
  @minLength(3)
  @maxLength(24)
  storage: string
  @minLength(2)
  @maxLength(64)
  blobEndpoint: string
  @minLength(2)
  @maxLength(64)
  queueEndpoint: string
}
