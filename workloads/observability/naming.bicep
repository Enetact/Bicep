@export()
@sealed()
type ResourceNames = {
  profile: 'standard-v2'
  @minLength(36)
  @maxLength(36)
  workbook: string
  @minLength(1)
  @maxLength(80)
  displayName: string
  @minLength(1)
  @maxLength(260)
  alert: string
}
