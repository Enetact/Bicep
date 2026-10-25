@export()
@sealed()
type ResourceNames = {
  profile: 'standard-v2'
  @minLength(3)
  @maxLength(63)
  capacity: string
  @minLength(1)
  @maxLength(64)
  workspacePrivateLink: string
  @minLength(2)
  @maxLength(64)
  workspaceEndpoint: string
}
