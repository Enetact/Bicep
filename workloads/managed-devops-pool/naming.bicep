@export()
@sealed()
type ResourceNames = {
  profile: 'standard-v2'
  @minLength(3)
  @maxLength(44)
  pool: string
}
