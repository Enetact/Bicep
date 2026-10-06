@export()
@sealed()
type ResourceNames = {
  profile: 'standard-v2'
  @minLength(3)
  @maxLength(24)
  storage: string
  @minLength(1)
  @maxLength(90)
  managedGroup: string
  @minLength(3)
  @maxLength(24)
  rootStorage: string
  @minLength(3)
  @maxLength(64)
  databricks: string
  @minLength(3)
  @maxLength(64)
  accessConnector: string
  @minLength(3)
  @maxLength(64)
  rootAccessConnector: string
  @minLength(3)
  @maxLength(63)
  factory: string
  @minLength(2)
  @maxLength(64)
  rootBlobEndpoint: string
  @minLength(2)
  @maxLength(64)
  rootDfsEndpoint: string
  @minLength(2)
  @maxLength(64)
  blobEndpoint: string
  @minLength(2)
  @maxLength(64)
  dfsEndpoint: string
  @minLength(2)
  @maxLength(64)
  databricksEndpoint: string
  @minLength(2)
  @maxLength(64)
  datafactoryEndpoint: string
  @minLength(2)
  @maxLength(64)
  adfportalEndpoint: string
}
