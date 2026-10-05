@export()
@sealed()
type ResourceNames = {
  profile: 'standard-v2'
  @minLength(3)
  @maxLength(24)
  hostStorage: string
  @minLength(3)
  @maxLength(24)
  uploadStorage: string
  @minLength(3)
  @maxLength(128)
  identity: string
  @minLength(2)
  @maxLength(60)
  app: string
  @minLength(1)
  @maxLength(60)
  plan: string
  @minLength(4)
  @maxLength(63)
  workspace: string
  @minLength(1)
  @maxLength(260)
  insights: string
  @minLength(1)
  @maxLength(260)
  functionErrors: string
  @minLength(1)
  @maxLength(260)
  recoveryReview: string
  @minLength(1)
  @maxLength(260)
  recoveryHeartbeat: string
  @minLength(2)
  @maxLength(64)
  vnet: string
  @minLength(1)
  @maxLength(80)
  integrationNsg: string
  @minLength(1)
  @maxLength(80)
  integrationSubnet: string
  @minLength(1)
  @maxLength(80)
  endpointSubnet: string
  @minLength(1)
  @maxLength(80)
  dnsLink: string
  @minLength(2)
  @maxLength(64)
  hostBlobEndpoint: string
  @minLength(2)
  @maxLength(64)
  hostQueueEndpoint: string
  @minLength(2)
  @maxLength(64)
  hostTableEndpoint: string
  @minLength(2)
  @maxLength(64)
  uploadBlobEndpoint: string
  @minLength(2)
  @maxLength(64)
  uploadQueueEndpoint: string
  @minLength(2)
  @maxLength(64)
  destinationBlobEndpoint: string
  @minLength(2)
  @maxLength(64)
  destinationDfsEndpoint: string
  @minLength(2)
  @maxLength(64)
  sitesEndpoint: string
}
