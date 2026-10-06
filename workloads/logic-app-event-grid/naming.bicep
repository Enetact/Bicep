@export()
@sealed()
type ResourceNames = {
  profile: 'standard-v2'
  @minLength(3)
  @maxLength(24)
  runtimeStorage: string
  @minLength(3)
  @maxLength(24)
  eventStorage: string
  @minLength(2)
  @maxLength(60)
  app: string
  @minLength(1)
  @maxLength(60)
  plan: string
  @minLength(3)
  @maxLength(50)
  topic: string
  @minLength(2)
  @maxLength(64)
  vnet: string
  @minLength(1)
  @maxLength(80)
  integrationNsg: string
  @minLength(4)
  @maxLength(63)
  workspace: string
  @minLength(1)
  @maxLength(80)
  integrationSubnet: string
  @minLength(1)
  @maxLength(80)
  endpointSubnet: string
  @minLength(1)
  @maxLength(80)
  dnsLink: string
  @minLength(1)
  @maxLength(260)
  insights: string
  @minLength(1)
  @maxLength(260)
  workflowFailures: string
  @minLength(1)
  @maxLength(260)
  quarantine: string
  @minLength(1)
  @maxLength(260)
  deliveryFailures: string
  @minLength(1)
  @maxLength(260)
  deadletters: string
  @minLength(1)
  @maxLength(260)
  backlog: string
  @minLength(2)
  @maxLength(64)
  runtimeBlobEndpoint: string
  @minLength(2)
  @maxLength(64)
  runtimeQueueEndpoint: string
  @minLength(2)
  @maxLength(64)
  runtimeTableEndpoint: string
  @minLength(2)
  @maxLength(64)
  runtimeFileEndpoint: string
  @minLength(2)
  @maxLength(64)
  eventBlobEndpoint: string
  @minLength(2)
  @maxLength(64)
  eventQueueEndpoint: string
  @minLength(2)
  @maxLength(64)
  sitesEndpoint: string
  @minLength(2)
  @maxLength(64)
  topicEndpoint: string
}
