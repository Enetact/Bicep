@export()
@sealed()
type ResourceNames = {
  profile: 'standard-v2'
  @minLength(3)
  @maxLength(24)
  storage: string
  @minLength(3)
  @maxLength(128)
  identity: string
  @minLength(2)
  @maxLength(60)
  app: string
  @minLength(1)
  @maxLength(60)
  plan: string
  @minLength(2)
  @maxLength(64)
  blobEndpoint: string
  @minLength(2)
  @maxLength(64)
  queueEndpoint: string
  @minLength(2)
  @maxLength(64)
  tableEndpoint: string
  @minLength(2)
  @maxLength(64)
  sitesEndpoint: string
}
