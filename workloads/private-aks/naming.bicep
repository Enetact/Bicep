@export()
@sealed()
type ResourceNames = {
  profile: 'standard-v2'
  @minLength(1)
  @maxLength(63)
  cluster: string
  @minLength(3)
  @maxLength(128)
  applicationIdentity: string
  @minLength(1)
  @maxLength(90)
  nodeGroup: string
}
