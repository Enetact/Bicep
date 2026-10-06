@export()
@sealed()
type ResourceNames = {
  profile: 'standard-v2'
  @minLength(3)
  @maxLength(24)
  vault: string
  @minLength(2)
  @maxLength(64)
  vaultEndpoint: string
}
